using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Platform;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Imaging;

namespace Glide.App;

/// <summary>Tab strip, drag/tear-off/attach, tab lifecycle and window cloning. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private void RebuildTabStrip()
    {
        if (TabStripPanel is null) return;
        TabStripPanel.Children.Clear();
        _workspace.ClosedHistoryLimit = Math.Clamp(_settings.ClosedTabHistoryLimit, 1, 100);
        // Avalonia Bounds and Width are already device-independent pixels. Do not divide tab
        // dimensions by RenderScaling: doing so made tabs visibly undersized on high-DPI displays.
        var configuredMin = Math.Clamp(_settings.TabMinWidth, 80, 240);
        const double emergencyMin = 18;
        var max = Math.Clamp(_settings.TabMaxWidth, 120, 400);
        if (max < configuredMin) max = configuredMin;
        var count = Math.Max(1, _workspace.Tabs.Count);
        var stripWidth = TabStripScroll.Bounds.Width > 20 ? TabStripScroll.Bounds.Width : Math.Max(20, Bounds.Width * 0.72);
        var utilityButtons = 0; // Back/Forward live outside the scrollable tab strip.
        var availableForTabs = Math.Max(emergencyMin, stripWidth - (NewTabButton.IsVisible ? 43 : 0) - utilityButtons * 35 - Math.Max(0, count - 1) * 3);
        var natural = availableForTabs / count;
        // Browser-style compression: tabs keep shrinking (dropping the close button on inactive
        // tabs, then the title) down to an icon-sized floor, and only then does the strip scroll.
        const double usableMin = 40;
        var floor = Math.Min(configuredMin, Math.Max(usableMin, emergencyMin));
        var preferred = natural >= configuredMin
            ? Math.Clamp(natural, configuredMin, max)
            : Math.Clamp(natural, floor, configuredMin);
        var sideTabs = SideTabsActive;
        if (sideTabs)
        {
            // vertical tabs use the full panel width; the panel scrolls instead of compressing tabs.
            preferred = SideTabPaneWidth - 14;
        }
        _tabStrip.TouchUnlessCycling(TabIds, _workspace.Active?.Id);

        var edgeHitTabs = !sideTabs && WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (NewTabButton is not null)
        {
            NewTabButton.Height = edgeHitTabs ? 44 : 36;
            NewTabButton.VerticalAlignment = edgeHitTabs ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
        }

        foreach (var tab in _workspace.Tabs)
        {
            var active = _workspace.Active?.Id == tab.Id;
            var container = new Border
            {
                DataContext = tab.Id,
                Width = preferred,
                Height = edgeHitTabs ? 44 : 36,
                VerticalAlignment = edgeHitTabs ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center,
                ClipToBounds = true
            };
            container.Classes.Add("glideTab");
            if (active) container.Classes.Add("active");

            // Chrome-like narrow-tab layout, computed from the real width so nothing overlaps:
            // inactive narrow tabs hide the close button, the active tab always keeps it (and drops
            // its icon first when tiny), titles are clipped to the space left and vanish below a few
            // pixels, and a lone icon is centred.
            const double iconSize = 14, iconGap = 7, closeSlot = 24;
            var showClose = active || preferred >= 90;
            var leftPad = preferred >= 80 ? 11d : 8d;
            var rightReserve = showClose ? closeSlot : leftPad;
            var contentAvailable = Math.Max(0, preferred - leftPad - rightReserve);
            var showIcon = contentAvailable >= iconSize;
            var textAvailable = contentAvailable - (showIcon ? iconSize + iconGap : 0);
            // A title is only shown alongside its icon; a tab too small for the icon shows neither.
            var showText = showIcon && textAvailable >= 8;
            var centreIcon = showIcon && !showText && !showClose;

            var grid = new Grid();
            var body = new Border
            {
                DataContext = tab.Id,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                Padding = centreIcon ? new Thickness(0) : new Thickness(leftPad, 0, rightReserve, 0)
            };
            body.Classes.Add("tabHit");
            body.PointerPressed += TabDragPointerPressed;
            body.PointerMoved += TabDragPointerMoved;
            body.PointerReleased += TabDragPointerReleased;
            body.AddHandler(InputElement.PointerReleasedEvent, TabRightReleasedTunnel, RoutingStrategies.Tunnel);
            body.PointerCaptureLost += TabDragPointerCaptureLost;

            var content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = iconGap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = centreIcon ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left,
                ClipToBounds = true
            };
            if (showIcon) content.Children.Add(new GlideIconView
            {
                Kind = tab.Kind switch { TabKind.Home => "Home", TabKind.Browser => "Browser", _ => "Image" },
                Width = 14,
                Height = 14,
                StrokeWidth = 1.5,
                IconBrush = new SolidColorBrush(Color.Parse(active ? CurrentAccentHex() : (IsDarkTheme() ? "#9AA6B2" : "#5D6974")))
            });
            var fullPath = TabFullPath(tab);
            var showFullPath = _settings.FullPathInTitle && fullPath is not null;
            if (showText) content.Children.Add(new TextBlock
            {
                Text = showFullPath ? fullPath : tab.Title,
                FontSize = 12.5,
                // Roomy tabs use an ellipsis (a full path keeps its file-name end); very narrow tabs
                // simply clip, like a browser, rather than showing only "…".
                TextTrimming = textAvailable < 36 ? TextTrimming.None
                    : showFullPath ? TextTrimming.PrefixCharacterEllipsis : TextTrimming.CharacterEllipsis,
                MaxWidth = textAvailable,
                ClipToBounds = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            body.Child = content;
            grid.Children.Add(body);

            var close = new Button
            {
                DataContext = tab.Id,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                Content = new GlideIconView
                {
                    Kind = "Close",
                    Width = 11,
                    Height = 11,
                    StrokeWidth = 1.4,
                    IconBrush = new SolidColorBrush(Color.Parse("#B4BEC8"))
                }
            };
            close.Classes.Add("tabClose");
            ToolTip.SetTip(close, "Close tab");
            close.Click += TabCloseClicked;
            close.IsVisible = showClose;
            if (active && !showIcon)
            {
                // A tiny active tab is just its close button, centred (as in Chrome).
                close.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                close.Margin = default;
            }
            grid.Children.Add(close);
            container.Child = grid;
            container.ContextMenu = BuildTabContextMenu(tab);
            var tabToolTip = BuildTabToolTip(tab, fullPath);
            ToolTip.SetTip(container, tabToolTip.ToolTip);
            // Side tabs: the tooltip opens beside the tab (above or below it would cover the neighbouring tabs). With hover
            // previews a top tab's tooltip opens under the tab, like a browser's tab preview. ToolTipGuard keeps this placement.
            if (sideTabs || _settings.TabHoverPreviews)
            {
                ToolTip.SetPlacement(container, sideTabs ? PlacementMode.Right : PlacementMode.Bottom);
                ToolTipGuard.SetKeepsOwnPlacement(container, true);
            }
            // The mouse hint, details and preview are refreshed every time the tooltip opens, so none is ever stale; an
            // animated GIF preview stops (and frees its frames) when it closes.
            container.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) =>
            {
                FillTabMouseHint(tabToolTip.Hint);
                FillTabPreview(tab, tabToolTip);
            });
            container.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToolTip.IsOpenProperty && e.NewValue is false) StopTabGifPreview(tabToolTip.Card.PictureHost);
            };
            // Round 3 #5: Ctrl (or the chosen key) + wheel over the tab resizes its open preview. Tunnel, so the tab strip's
            // own wheel scrolling never sees those notches.
            ToolTipGuard.SetKeepsOpenOnWheel(container, e => WheelResizesTabPreview(container, tabToolTip, e));
            container.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (TryResizeTabPreviewWithWheel(container, tabToolTip, e)) e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            ToolTip.SetTip(close, "Close tab");
            TabStripPanel.Children.Add(container);
            if (active)
            {
                var activeContainer = container;
                Dispatcher.UIThread.Post(() => { if (activeContainer.IsAttachedToVisualTree()) activeContainer.BringIntoView(); }, DispatcherPriority.Loaded);
            }
        }
        Dispatcher.UIThread.Post(UpdateTabOverflowButtons, DispatcherPriority.Render);
        LayoutTitleBarTextOverlay();
    }

    private static string? TabFullPath(TabState tab) => tab switch
    {
        ImageTabState image when !string.IsNullOrWhiteSpace(image.Path) => image.Path,
        BrowserTabState browser when !string.IsNullOrWhiteSpace(browser.Folder) => browser.Folder,
        _ => null
    };

    /// <summary>Name / detail font sizes for the tab tooltip ("Tab tooltip text size"); the mouse hint is a notch smaller.</summary>
    internal static (double Name, double Detail) TabToolTipFontSizes(string? choice) => choice switch
    {
        "Small" => (12, 11),
        "Large" => (18, 14),
        _ => (14, 12)
    };

    /// <summary>
    /// Tab tooltip card (approved mockup docs/mockups/tab-hover-preview-5.0.6.md, laid out by TabHoverCard): the hover
    /// preview slot (filled when it opens, MainWindow.TabPreview.cs) edge to edge on top, then the name in bold, the full
    /// folder path dimmed (a picture's folder, or an Explorer tab's own folder), the details line (pixel and file size,
    /// or an Explorer tab's item count; filled when it opens) and one faint line of mouse hints. The name and path are
    /// never shortened: they wrap and the card grows taller. The tooltip has no padding so the preview reaches its edges.
    /// </summary>
    private TabToolTipParts BuildTabToolTip(TabState tab, string? fullPath)
    {
        var (nameSize, detailSize) = TabToolTipFontSizes(_settings.TabToolTipTextSize);
        var (name, folder) = TabToolTipLines(tab.Title, fullPath);
        if (tab is BrowserTabState && fullPath is not null) folder = fullPath;
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(10, 8) };
        if (!string.IsNullOrWhiteSpace(name))
            text.Children.Add(new TextBlock { Text = name, FontSize = nameSize, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(folder))
            text.Children.Add(new TextBlock { Text = folder, FontSize = detailSize, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var details = new TextBlock { FontSize = detailSize, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        text.Children.Add(details);
        var hint = new TextBlock { FontSize = Math.Max(10, detailSize - 1), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 3, 0, 0) };
        FillTabMouseHint(hint);
        text.Children.Add(hint);
        var card = new TabHoverCard(CreateTabPreviewHost(), text, hint);
        var toolTip = new ToolTip
        {
            Content = card,
            Padding = new Thickness(0),
            MaxWidth = double.PositiveInfinity,
            CornerRadius = new CornerRadius(TabToolTipCornerRadius),
        };
        return new TabToolTipParts(toolTip, card, details, hint);
    }

    /// <summary>The tooltip's name and folder lines: the item's own name and the folder that contains it.</summary>
    internal static (string? Name, string? Folder) TabToolTipLines(string? title, string? fullPath)
    {
        if (fullPath is null) return (string.IsNullOrWhiteSpace(title) ? null : title, null);
        var trimmed = fullPath.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(title) ? fullPath : title;
        string? folder;
        try { folder = System.IO.Path.GetDirectoryName(trimmed); } catch (ArgumentException) { folder = null; }
        return (name, string.IsNullOrWhiteSpace(folder) ? null : folder);
    }

    private static readonly string[] TabToolTipSizeSteps = ["Small", "Medium", "Large"];

    /// <summary>Steps the tab tooltip text size (actions tabs.tooltipLarger / tabs.tooltipSmaller) and previews it.</summary>
    private void StepTabToolTipTextSize(int direction)
    {
        var index = Array.IndexOf(TabToolTipSizeSteps, _settings.TabToolTipTextSize);
        if (index < 0) index = 1;
        var next = TabToolTipSizeSteps[Math.Clamp(index + direction, 0, TabToolTipSizeSteps.Length - 1)];
        if (!string.Equals(next, _settings.TabToolTipTextSize, StringComparison.Ordinal))
        {
            _settings.TabToolTipTextSize = next;
            SaveSettingsAndPublish();
            RebuildTabStrip();
        }
        ShowActiveTabToolTipPreview();
        ShowViewerNotice($"Tab tooltip text: {next}");
    }

    private DispatcherTimer? _tabToolTipPreviewTimer;

    /// <summary>Opens the active tab's tooltip for a few seconds so a size change can be judged live.</summary>
    private void ShowActiveTabToolTipPreview()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var activeId = _workspace.Active?.Id;
            var border = TabStripPanel.Children.OfType<Border>().FirstOrDefault(b => b.DataContext is Guid id && id == activeId);
            if (border is null || !border.IsVisible) return;
            ToolTip.SetIsOpen(border, true);
            _tabToolTipPreviewTimer?.Stop();
            _tabToolTipPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _tabToolTipPreviewTimer.Tick += (_, _) =>
            {
                _tabToolTipPreviewTimer?.Stop();
                ToolTip.SetIsOpen(border, false);
            };
            _tabToolTipPreviewTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Tab tooltip hint that reflects which mouse actions are actually enabled (live settings, custom gestures included)
    /// and the chosen preview-resize key: one line of key chips (BindingHints.TabMouseHintChips), approved design B
    /// (5.1.0): [Drag] move · [Middle] close · [Right] more · [Ctrl+Wheel] size.
    /// </summary>
    private void FillTabMouseHint(TextBlock hint)
    {
        var chips = BindingHints.TabMouseHintChips(_settings.Gestures, _settings.CustomGestures, _settings.TabPreviewResizeModifier);
        var muted = new SolidColorBrush(Color.FromArgb(0xB0, 0xA8, 0xA8, 0xA8));
        var inlines = hint.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        inlines.Clear();
        for (var i = 0; i < chips.Count; i++)
        {
            if (i > 0) inlines.Add(new Avalonia.Controls.Documents.Run(" \u00b7 ") { Foreground = muted });
            inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0),
                Child = new TextBlock { Text = chips[i].Input, FontSize = hint.FontSize, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) },
            }) { BaselineAlignment = BaselineAlignment.Center });
            inlines.Add(new Avalonia.Controls.Documents.Run(" " + chips[i].Action) { Foreground = muted });
        }
    }

    // ---- Ctrl+Tab most-recently-used cycling ---------------------------------------------------
    // Stage D4: MRU, overflow, drag-slide and tear-off decisions live in Glide.Core.TabStripController.
    private readonly TabStripController _tabStrip = new();
    private bool _lastKeyDownHadCtrl;

    private IEnumerable<Guid> TabIds => _workspace.Tabs.Select(t => t.Id);

    /// <summary>Moves the active tab to the front of the MRU list.</summary>
    private void TouchTabMru() => _tabStrip.Touch(TabIds, _workspace.Active?.Id);

    /// <summary>Releasing Ctrl ends an MRU cycle and commits the landed-on tab as most recent.</summary>
    private void EndTabMruCycle() => _tabStrip.EndCycle(TabIds, _workspace.Active?.Id);

    private void TabOverflowLeftClicked(object? sender, RoutedEventArgs e)
    {
        ScrollTabStrip(-1);
        e.Handled = true;
    }

    private void TabOverflowRightClicked(object? sender, RoutedEventArgs e)
    {
        ScrollTabStrip(1);
        e.Handled = true;
    }

    private void ScrollTabStrip(int direction)
    {
        if (!_settings.TabOverflowArrows || direction == 0) return;
        var next = TabStripController.ScrollOffset(TabStripScroll.Viewport.Width, TabStripScroll.Extent.Width,
            TabStripScroll.Offset.X, direction, _settings.TabMaxWidth);
        TabStripScroll.Offset = new Vector(next, TabStripScroll.Offset.Y);
        Dispatcher.UIThread.Post(UpdateTabOverflowButtons, DispatcherPriority.Render);
    }

    private void UpdateTabOverflowButtons()
    {
        if (TabOverflowLeftButton is null || TabOverflowRightButton is null || TabStripScroll is null) return;
        // At very small widths the three caption buttons and actual tab bodies outrank overflow
        // arrows. Hide the arrows and let the tabs compress/scroll rather than spending 56 DIP on
        // navigation chrome while the user is deliberately squeezing the window.
        var allowed = TabStripController.OverflowArrowsAllowed(SideTabsActive, _settings.TabsEnabled, _settings.TabOverflowArrows, Bounds.Width);
        var (left, right) = TabStripController.OverflowArrows(allowed, TabStripScroll.Viewport.Width, TabStripScroll.Extent.Width, TabStripScroll.Offset.X);
        TabOverflowLeftButton.IsVisible = left;
        TabOverflowRightButton.IsVisible = right;
    }

    private async void TabDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: Guid id } body) return;

        // The drag controller captures the pointer on the first press, which means Avalonia's
        // higher-level DoubleTapped gesture is not guaranteed to fire for a tab body. Detect the
        // native click count at the press boundary instead so double-click-to-close remains reliable
        // without weakening drag/reorder behaviour.
        if (e.GetCurrentPoint(body).Properties.IsLeftButtonPressed && e.ClickCount >= 2)
        {
            var configuredDouble = ResolveGestureSlot("tabs.doubleClick", e.KeyModifiers);
            if (!string.Equals(configuredDouble, GestureCatalog.Nothing, StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                _tabDrag.Clear();
                await ExecuteTabGestureAsync(id, configuredDouble, "double_click");
                return;
            }
        }

        // Right press on a tab: Default leaves the tab context menu to Avalonia (opened on release).
        if (e.GetCurrentPoint(body).Properties.IsRightButtonPressed)
            _tabRightPressAction = ResolveGestureSlot("tabs.rightClick", e.KeyModifiers);
        if (e.GetCurrentPoint(body).Properties.IsMiddleButtonPressed) _lastTabMiddleModifiers = e.KeyModifiers;

        await _tabDrag.PointerPressedAsync(id, body, e);
    }

    private string _tabRightPressAction = GestureCatalog.Default;
    private KeyModifiers _lastTabMiddleModifiers;

    /// <summary>
    /// Runs a Tabs-area action for the clicked tab. Default closes the tab (the 4.7 double-click and
    /// middle-click behaviour); "Close tab", "Duplicate tab" and "Move tab to a new window" act on the
    /// clicked tab rather than the active one.
    /// </summary>
    private async Task ExecuteTabGestureAsync(Guid id, string actionId, string reason)
    {
        switch (GestureCatalog.NormalizeAction(actionId))
        {
            case GestureCatalog.Default:
            case GestureCatalog.CloseTab:
                await CloseTabWithPolicyAsync(id, reason);
                return;
            case GestureCatalog.Nothing:
                return;
            case GestureCatalog.DetachTab:
                await DetachTabAsync(id, this.PointToScreen(new Point(Bounds.Width / 2, 48)), fromMenu: true);
                return;
            case GestureCatalog.DuplicateTab:
                if (_workspace.Active?.Id != id) { CaptureActiveTabRuntimeState(); if (_workspace.Select(id)) await ActivateWorkspaceAsync(); }
                await DuplicateActiveTabAsync();
                return;
            case GestureCatalog.ContextMenu:
                if (_workspace.Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
                    BuildTabContextMenu(tab).Open(TabStripPanel.Children.OfType<Border>().FirstOrDefault(b => Equals(b.DataContext, id)) ?? (Control)TabStripPanel);
                return;
            default:
                await ExecuteGestureActionAsync(actionId);
                return;
        }
    }

    private async void TabDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Border { DataContext: Guid id } body)
            await _tabDrag.PointerMovedAsync(id, body, e);
    }

    private int ApplyTabDragPreview(Guid draggedId, double draggedOffsetX)
    {
        var borders = TabStripPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("glideTab")).ToList();
        var moving = borders.FirstOrDefault(b => Equals(b.DataContext, draggedId));
        // Index in the strip that is actually drawn (same list the neighbours are animated from).
        var originalIndex = moving is null ? -1 : borders.IndexOf(moving);
        if (moving is null || originalIndex < 0) return -1;

        var vertical = SideTabsActive;
        TranslateTransform Along(double offset) => vertical ? new TranslateTransform(0, offset) : new TranslateTransform(offset, 0);
        moving.RenderTransform = Along(draggedOffsetX);
        moving.ZIndex = 50;
        foreach (var border in borders)
        {
            if (Equals(border.DataContext, draggedId)) continue;
            border.RenderTransform = null;
            border.ZIndex = 0;
        }

        // Cross-window attachment owns a separate highlight. Inside one strip, move neighbours
        // continuously instead of snapping them a whole slot at a threshold. The first neighbour
        // begins yielding after roughly one-quarter tab width and reaches its destination by
        // three-quarters; later neighbours follow the same rolling window. This tracks Chromium's
        // physical feel much more closely while keeping the dragged tab exactly under the pointer.
        if (_tabAttach.Target is not null) return -1;
        var slot = Math.Max(1, (vertical ? moving.Bounds.Height : moving.Bounds.Width) + 3);
        // the slot the preview shows (a neighbour counts as swapped once it is half way across)
        // is exactly what release commits, so a drop never snaps back after tabs visibly swapped.
        var previewInsertIndex = Glide.Core.TabDragPolicy.PreviewInsertIndex(originalIndex, borders.Count, draggedOffsetX, slot);
        var offsets = TabStripController.NeighbourOffsets(originalIndex, borders.Count, draggedOffsetX, slot);
        for (var i = 0; i < borders.Count; i++)
            if (i != originalIndex && offsets[i] != 0) borders[i].RenderTransform = Along(offsets[i]);
        return previewInsertIndex;
    }

    private async void TabDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border { DataContext: Guid id })
            await _tabDrag.PointerReleasedAsync(id, e);
    }

    /// <summary>
    /// Tabs right-click row. Runs on the tunnel route so a non-default action marks the release
    /// handled before Avalonia's bubble-phase ContextRequested could open the tab menu. Default
    /// leaves the release alone, so the tab context menu opens exactly as before.
    /// </summary>
    private async void TabRightReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border { DataContext: Guid id } || e.InitialPressMouseButton != MouseButton.Right) return;
        var configured = _tabRightPressAction;
        _tabRightPressAction = GestureCatalog.Default;
        if (string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase)) return;
        e.Handled = true;
        await ExecuteTabGestureAsync(id, configured, "right_click");
    }

    private void TabDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Border { DataContext: Guid id }) _tabDrag.PointerCaptureLost(id);
    }

    private void ClearTabDragState() => _tabDrag.Clear();

    private void ClearTabVisuals()
    {
        foreach (var border in TabStripPanel.Children.OfType<Border>())
        {
            border.RenderTransform = null;
            border.ZIndex = 0;
            border.Classes.Remove("dragging");
        }
    }

    private TabDragHost CreateTabDragHost() => new()
    {
        TabCount = () => _workspace.Tabs.Count,
        IndexOfTab = id => _workspace.Tabs.ToList().FindIndex(x => x.Id == id),
        ContainsTab = id => _workspace.Tabs.Any(x => x.Id == id),
        IsFullscreen = () => WindowState == WindowState.FullScreen,
        TabAttachEnabled = () => _settings.TabAttachEnabled,
        TabDetachEnabled = () => _settings.TabDetachEnabled,
        SelectForDrag = id =>
        {
            if (_workspace.Active?.Id != id) CaptureActiveTabRuntimeState();
            if (!_workspace.Select(id)) return;
            foreach (var border in TabStripPanel.Children.OfType<Border>())
            {
                border.Classes.Remove("active");
                if (Equals(border.DataContext, id)) border.Classes.Add("active");
            }
        },
        CloseMiddleAsync = id => ExecuteTabGestureAsync(id, ResolveGestureSlot("tabs.middleClick", _lastTabMiddleModifiers), "middle_click"),
        Capture = (body, pointer) => pointer.Capture(body),
        ReleaseCapture = pointer => pointer.Capture(null),
        IsLeftPressed = (body, e) => e.GetCurrentPoint(body).Properties.IsLeftButtonPressed,
        // Vertical tabs: the drag axis is Y, so the controller's "X" is the strip's own axis.
        StripPoint = e => SideTabsActive ? new Point(e.GetPosition(TabStripPanel).Y, e.GetPosition(TabStripPanel).X) : e.GetPosition(TabStripPanel),
        ScreenPoint = e => this.PointToScreen(e.GetPosition(this)),
        IsBeyondTearOff = IsBeyondTabTearOffThreshold,
        TabStripContains = point => GetTabStripScreenRect().Contains(point),
        GetInsertIndex = GetTabInsertIndex,
        ApplyPreview = (id, offset, _) => ApplyTabDragPreview(id, offset),
        ClearVisuals = ClearTabVisuals,
        BeginSoleNativeMoveAsync = BeginHeldSoleTabNativeMoveAsync,
        BeginTearOffAsync = BeginHeldTabTearOffAsync,
        AttachAsync = async (id, target, index) =>
        {
            CaptureTabRuntimeState(id);
            var transferred = RemoveTabForTransfer(id);
            if (transferred is null) return false;
            await target.AcceptTransferredTabAsync(transferred, Math.Max(0, index));
            _diagnostics.Write("tabs", "attached", new { id, target = target.Title, targetIndex = index });
            HandleSourceAfterTransfer(successfulCrossWindowTransfer: true);
            return true;
        },
        DetachAsync = DetachTabAsync,
        ReorderAsync = (id, index) =>
        {
            _workspace.Move(id, index);
            _diagnostics.Write("tabs", "reordered", new { id, targetIndex = index });
            return Task.CompletedTask;
        },
        ActivateAsync = ActivateWorkspaceAsync,
        RebuildTabs = RebuildTabStrip,
        Diagnostic = (name, data) => _diagnostics.Write("tabs", name, data)
    };

    private async Task<bool> BeginHeldTabTearOffAsync(Guid id, PixelPoint cursor)
    {
        CaptureTabRuntimeState(id);
        var transferred = RemoveTabForTransfer(id);
        if (transferred is null) return false;

        var detached = new MainWindow(transferred, _settings.CloneState(), deferWorkspaceActivation: true);
        CopyWindowGeometryTo(detached);
        ShowSecondaryWindow(detached);
        PlaceDetachedWindow(detached, cursor, grabX: 120, grabY: 22);
        RebuildTabStrip();
        _diagnostics.Write("tabs", "held_tearoff_started", new { id, screenX = cursor.X, screenY = cursor.Y, moveLoop = "native-windows" });
        await detached.RunNativeDetachedTabDragAsync(transferred.Id, this, cursor);
        return true;
    }

    private async Task BeginHeldSoleTabNativeMoveAsync(Guid id, PixelPoint cursor)
    {
        _physicalDraggedTabId = id;
        _physicalDragSource = this;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = false;
        UpdateNativeDetachedAttachTarget(cursor);
        PositionChanged += SoleTabNativePositionChanged;
        _diagnostics.Write("tabs", "sole_native_move_started", new { id, screenX = cursor.X, screenY = cursor.Y, moveLoop = "native-windows" });

        var nativeMoveStarted = TryRunNativeWindowMoveLoop();
        PositionChanged -= SoleTabNativePositionChanged;
        if (!TryGetPhysicalCursor(out var releaseCursor)) releaseCursor = cursor;
        UpdateNativeDetachedAttachTarget(releaseCursor);
        if (!nativeMoveStarted)
            _diagnostics.Write("tabs", "sole_native_move_unavailable", new { id, platform = Environment.OSVersion.Platform.ToString() });

        await CompleteSoleTabNativeMoveAsync(id, releaseCursor);
    }

    private void SoleTabNativePositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_physicalDraggedTabId is null || !TryGetPhysicalCursor(out var cursor)) return;
        UpdateNativeDetachedAttachTarget(cursor);
    }

    private async Task CompleteSoleTabNativeMoveAsync(Guid id, PixelPoint cursor)
    {
        var target = _physicalAttach.Target;
        var targetIndex = _physicalAttach.TargetIndex;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = true;
        try
        {
            if (target is { IsVisible: true } && target.CanAcceptTabAttach && _settings.TabAttachEnabled)
            {
                CaptureTabRuntimeState(id);
                var transferred = RemoveTabForTransfer(id);
                if (transferred is not null)
                {
                    await target.AcceptTransferredTabAsync(transferred, Math.Max(0, targetIndex));
                    _diagnostics.Write("tabs", "sole_native_drag_attached", new { id, target = target.Title, targetIndex });
                    HandleSourceAfterTransfer(successfulCrossWindowTransfer: true);
                }
            }
            else
            {
                await ActivateWorkspaceAsync();
                Activate();
                _diagnostics.Write("tabs", "sole_native_drag_committed", new { id, screenX = cursor.X, screenY = cursor.Y });
            }
        }
        finally
        {
            _physicalDraggedTabId = null;
            _physicalDragSource = null;
            if (IsVisible) RebuildTabStrip();
        }
    }

    private async Task RunNativeDetachedTabDragAsync(Guid tabId, MainWindow source, PixelPoint cursor)
    {
        _physicalDraggedTabId = tabId;
        _physicalDragSource = source;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = false;
        Position = new PixelPoint(Math.Max(0, cursor.X - 120), Math.Max(0, cursor.Y - 22));
        UpdateNativeDetachedAttachTarget(cursor);
        PositionChanged += NativeDetachedPositionChanged;
        _diagnostics.Write("tabs", "native_detached_move_started", new { tabId, source = source.Title, screenX = cursor.X, screenY = cursor.Y });

        var nativeMoveStarted = TryRunNativeWindowMoveLoop();
        PositionChanged -= NativeDetachedPositionChanged;
        if (!TryGetPhysicalCursor(out var releaseCursor)) releaseCursor = cursor;
        UpdateNativeDetachedAttachTarget(releaseCursor);

        if (!nativeMoveStarted)
            _diagnostics.Write("tabs", "native_detached_move_unavailable", new { tabId, platform = Environment.OSVersion.Platform.ToString() });

        await CompleteNativeDetachedTabDragAsync(tabId, releaseCursor);
    }

    private void NativeDetachedPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_physicalDraggedTabId is null || !TryGetPhysicalCursor(out var cursor)) return;
        UpdateNativeDetachedAttachTarget(cursor);
    }

    private void UpdateNativeDetachedAttachTarget(PixelPoint cursor)
    {
        _physicalAttach.Update(cursor, _settings.TabAttachEnabled, "native_merge_target_entered", "native_merge_target_left", "tabs");
    }

    private bool TryRunNativeWindowMoveLoop()
    {
        if (!OperatingSystem.IsWindows() || (Platform.Native.User32.GetAsyncKeyState(VkLButton) & 0x8000) == 0) return false;
        var platform = TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return false;
        try
        {
            Platform.Native.User32.ReleaseCapture();
            EnterNativeMoveLoopGuard();
            try {            Platform.Native.User32.SendMessage(platform.Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);}
            finally { LeaveNativeMoveLoopGuard(); }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task CompleteNativeDetachedTabDragAsync(Guid tabId, PixelPoint cursor)
    {
        var attachTarget = _physicalAttach.Target;
        var attachIndex = _physicalAttach.TargetIndex;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = true;

        var attachedElsewhere = false;
        try
        {
            if (attachTarget is { IsVisible: true } && attachTarget.CanAcceptTabAttach && _settings.TabAttachEnabled)
            {
                var transferred = RemoveTabForTransfer(tabId);
                if (transferred is not null)
                {
                    await attachTarget.AcceptTransferredTabAsync(transferred, Math.Max(0, attachIndex));
                    attachedElsewhere = true;
                    _diagnostics.Write("tabs", "native_drag_attached", new { tabId, target = attachTarget.Title, attachIndex });
                }
                if (_workspace.Tabs.Count == 0) Close();
            }
            else
            {
                _deferWorkspaceActivation = false;
                await ActivateWorkspaceAsync();
                Activate();
                _diagnostics.Write("tabs", "native_drag_detached_committed", new { tabId, screenX = cursor.X, screenY = cursor.Y });
            }
        }
        finally
        {
            var sourceWindow = _physicalDragSource;
            _physicalDraggedTabId = null;
            _physicalDragSource = null;
            if (sourceWindow is { IsVisible: true })
            {
                sourceWindow.HandleSourceAfterTransfer(successfulCrossWindowTransfer: attachedElsewhere);
                if (sourceWindow.IsVisible && sourceWindow._workspace.Tabs.Count > 0)
                    await sourceWindow.ActivateWorkspaceAsync();
            }
        }
    }

    private bool IsBeyondTabTearOffThreshold(PixelPoint point)
    {
        var inside = GetWindowScreenRect().Contains(point);
        if (!inside) return true;
        var strip = GetTabStripScreenRect();
        return TabStripController.BeyondTearOffThreshold(inside, SideTabsActive, point.X, point.Y,
            strip.X, strip.Y, strip.Width, strip.Height, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
    }

    private ScreenPixelRect GetWindowScreenRect()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        // Position is physical; Bounds is logical. Include a small caption/border allowance so a
        // slight vertical tab drag does not accidentally tear off on high-DPI desktops.
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        return new ScreenPixelRect(Position.X - 8, Position.Y - 8, width + 16, height + 48);
    }

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScClose = 0xF060;
    private const int ScRestore = 0xF120;
    private const int HtCaption = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;

    // user32 entry points: Platform/Native/User32.cs (Stage D9).

    private static (bool Left, bool Right, bool Middle) PhysicalMouseButtons(PointerPoint point)
    {
        var kind = point.Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed) return (true, false, false);
        if (kind == PointerUpdateKind.RightButtonPressed) return (false, true, false);
        if (kind == PointerUpdateKind.MiddleButtonPressed) return (false, false, true);

        return (point.Properties.IsLeftButtonPressed, point.Properties.IsRightButtonPressed, point.Properties.IsMiddleButtonPressed);
    }

    private static bool TryGetPhysicalCursor(out PixelPoint point)
    {
        if (Platform.Native.User32.GetCursorPos(out var native))
        {
            point = new PixelPoint(native.X, native.Y);
            return true;
        }
        point = default;
        return false;
    }

    internal ScreenPixelRect GetTabStripScreenRect()
    {
        try
        {
            var originInWindow = TabStripScroll.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
            var topLeft = this.PointToScreen(originInWindow);
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return new ScreenPixelRect(topLeft.X, topLeft.Y, Math.Max(1, (int)Math.Ceiling(TabStripScroll.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(TabStripScroll.Bounds.Height * scale)));
        }
        catch
        {
            return new ScreenPixelRect(Position.X, Position.Y, Math.Max(1, (int)Width), 50);
        }
    }

    /// <summary>
    /// Forgiving physical-pixel hit zone used only while a tab is being dragged. It covers the
    /// full tab-strip width plus a modest horizontal margin and most of the chrome/content seam;
    /// the top edge remains inset so dropping over a native title bar can never merge a window.
    /// </summary>
    internal ScreenPixelRect GetTabAttachZoneScreenRect()
    {
        var strip = GetTabStripScreenRect();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        var horizontal = Math.Max(24, (int)Math.Round(28 * scale));
        var top = Math.Max(4, (int)Math.Round(6 * scale));
        var bottom = Math.Max(56, (int)Math.Round(78 * scale));
        return strip.Inflate(horizontal, top, bottom);
    }

    internal int GetTabInsertIndex(PixelPoint screenPoint)
    {
        if (_workspace.Tabs.Count == 0) return 0;
        var pointInWindow = this.PointToClient(screenPoint);
        var panelOrigin = TabStripPanel.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var vertical = SideTabsActive;
        var clientX = vertical ? pointInWindow.Y - panelOrigin.Y : pointInWindow.X - panelOrigin.X;
        return Glide.Core.TabDragPolicy.InsertIndexFromPointer(TabSlotsAlongStrip(), clientX);
    }

    /// <summary>Layout slots (start, length) of the tab bodies along the strip axis, untransformed (drag previews use RenderTransform only).</summary>
    private List<(double Start, double Length)> TabSlotsAlongStrip()
    {
        var vertical = SideTabsActive;
        return TabStripPanel.Children.OfType<Border>().Where(x => x.Classes.Contains("glideTab"))
            .Select(b => vertical ? (b.Bounds.Y, b.Bounds.Height) : (b.Bounds.X, b.Bounds.Width))
            .ToList();
    }

    internal void SetTabAttachHighlight(bool enabled)
    {
        if (enabled)
        {
            if (!ChromeBorder.Classes.Contains("attachTargetStrip")) ChromeBorder.Classes.Add("attachTargetStrip");
        }
        else
        {
            ChromeBorder.Classes.Remove("attachTargetStrip");
            SetTabAttachInsertMarker(-1);
        }
    }

    private Border? _tabInsertMarker;

    /// <summary>
    /// Shows (index &gt;= 0) or hides the insertion marker for a tab dropped into this window from another one. The
    /// marker is drawn at exactly the index the drop commits (both come from <see cref="GetTabInsertIndex"/>).
    /// </summary>
    internal void SetTabAttachInsertMarker(int index)
    {
        if (index < 0)
        {
            if (_tabInsertMarker is not null) _tabInsertMarker.IsVisible = false;
            return;
        }
        var layer = Avalonia.Controls.Primitives.AdornerLayer.GetAdornerLayer(TabStripPanel);
        if (layer is null) return;
        if (_tabInsertMarker is null)
        {
            _tabInsertMarker = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1.5),
                ZIndex = 100,
            };
            _tabInsertMarker.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccent"));
            Avalonia.Controls.Primitives.AdornerLayer.SetAdornedElement(_tabInsertMarker, TabStripPanel);
        }
        if (!layer.Children.Contains(_tabInsertMarker)) layer.Children.Add(_tabInsertMarker);
        var vertical = SideTabsActive;
        var position = Glide.Core.TabDragPolicy.InsertMarkerPosition(TabSlotsAlongStrip(), index, TabStripPanel.Spacing);
        const double thickness = 3;
        _tabInsertMarker.HorizontalAlignment = vertical ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        _tabInsertMarker.VerticalAlignment = vertical ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
        _tabInsertMarker.Width = vertical ? double.NaN : thickness;
        _tabInsertMarker.Height = vertical ? thickness : double.NaN;
        _tabInsertMarker.Margin = vertical
            ? new Thickness(0, Math.Max(0, position - thickness / 2), 0, 0)
            : new Thickness(Math.Max(0, position - thickness / 2), 4, 0, 4);
        _tabInsertMarker.IsVisible = true;
    }

    internal void OnAttachTargetClosed(MainWindow closedWindow)
    {
        var ordinaryTarget = _tabAttach.Target;
        if (ordinaryTarget is not null && ReferenceEquals(ordinaryTarget, closedWindow))
        {
            _tabDrag.ClearAttachTarget();
            _diagnostics.Write("tabs", "merge_target_closed", new { target = closedWindow.Title, mode = "ordinary" });
        }

        var nativeTarget = _physicalAttach.Target;
        if (nativeTarget is not null && ReferenceEquals(nativeTarget, closedWindow))
        {
            _physicalAttach.Clear();
            _diagnostics.Write("tabs", "merge_target_closed", new { target = closedWindow.Title, mode = "native" });
        }
    }

    internal async Task AcceptTransferredTabAsync(TabState tab, int index)
    {
        _workspace.InsertTransferred(tab, Math.Clamp(index, 0, _workspace.Tabs.Count), activate: true);
        _diagnostics.Write("tabs", "transfer_received", new { tab.Id, tab.Kind, index, count = _workspace.Tabs.Count });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        Activate();
    }

    private async Task DetachTabAsync(Guid id, PixelPoint screenPoint) => await DetachTabAsync(id, screenPoint, fromMenu: false);

    /// <summary>
    /// Moves a tab to a new window (tab menu, the detach command / gesture, a drag released outside the strip). Like a
    /// drag-out, the tab leaves this window's strip at once and this window shows the tab that closing it would show
    /// (or Home / closes when it was the last tab). The menu and command place the new window by
    /// <see cref="DetachedWindowPlacement.ForMenuMove"/>, a drop at the pointer; both stay inside the work area.
    /// </summary>
    private async Task DetachTabAsync(Guid id, PixelPoint screenPoint, bool fromMenu)
    {
        if (!_settings.TabDetachEnabled) return;
        var wasActive = _workspace.Active?.Id == id;
        CaptureTabRuntimeState(id);
        var transferred = RemoveTabForTransfer(id);
        if (transferred is null) return;
        var detached = new MainWindow(transferred, _settings.CloneState());
        CopyWindowGeometryTo(detached);
        if (fromMenu)
        {
            detached.WindowState = WindowState.Normal;
            if (_normalPlacementKnown && WindowState != WindowState.Normal)
            {
                detached.Width = Math.Max(detached.MinWidth, _lastNormalWindowSize.Width);
                detached.Height = Math.Max(detached.MinHeight, _lastNormalWindowSize.Height);
            }
        }
        PlaceDetachedWindow(detached, fromMenu ? null : screenPoint, grabX: 100, grabY: 24);
        ShowSecondaryWindow(detached);
        _diagnostics.Write("tabs", "detached", new { id, fromMenu, screenX = screenPoint.X, screenY = screenPoint.Y });
        HandleSourceAfterTransfer();
        if (IsVisible)
        {
            RebuildTabStrip();
            if (wasActive || _workspace.Tabs.Count > 0) await ActivateWorkspaceAsync();
        }
    }

    /// <summary>Positions a window opened for a moved tab inside the monitor's work area (pure math: DetachedWindowPlacement).</summary>
    private void PlaceDetachedWindow(MainWindow detached, PixelPoint? pointer, int grabX, int grabY)
    {
        var anchor = pointer ?? (WindowState == WindowState.Normal || !_normalPlacementKnown ? Position : _lastNormalWindowPosition);
        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            detached.Position = pointer is { } p ? new PixelPoint(Math.Max(0, p.X - grabX), Math.Max(0, p.Y - grabY)) : anchor;
            return;
        }
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var work = new PixelBox(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);
        var width = (int)Math.Round((double.IsNaN(detached.Width) ? Bounds.Width : detached.Width) * scale);
        var height = (int)Math.Round((double.IsNaN(detached.Height) ? Bounds.Height : detached.Height) * scale);
        var box = pointer is { } at
            ? DetachedWindowPlacement.ForDragOut(at.X, at.Y, (int)(grabX * scale), (int)(grabY * scale), width, height, work)
            : DetachedWindowPlacement.ForMenuMove(new PixelBox(anchor.X, anchor.Y, width, height), work);
        if (box.Width < width) detached.Width = box.Width / scale;
        if (box.Height < height) detached.Height = box.Height / scale;
        detached.Position = new PixelPoint(box.X, box.Y);
    }

    private void HandleSourceAfterTransfer(bool successfulCrossWindowTransfer = false)
    {
        if (_workspace.Tabs.Count > 0) return;

        // Legacy invariant: if a window's final tab successfully joins another existing Glide
        // window, the source closes automatically. Do not manufacture a Home tab in that case.
        if (successfulCrossWindowTransfer && GlideWindowRegistry.Snapshot().Count > 1)
        {
            Close();
            return;
        }

        if (_settings.CloseEmptyWindowAfterDetach && GlideWindowRegistry.Snapshot().Count > 1)
            Close();
        else
            _workspace.EnsureHomeIfEmpty();
    }

    private ContextMenu BuildTabContextMenu(TabState tab)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        // Each item is the registry action applied to THIS tab (which may not be the active one); the header carries
        // the action's live shortcut (5.0.x: every item shows its binding, not only three of them).
        MenuItem Item(string label, string actionId, Action action, bool enabled = true, string? icon = null)
        {
            var item = MenuActions.Tag(new MenuItem { Header = MenuActions.Header(label, ShortcutForAction(actionId)), IsEnabled = enabled }, actionId);
            if (icon is not null) item.Icon = MenuIcon(icon);
            item.Click += (_, _) => action();
            return item;
        }
        items.Add(Item("Duplicate tab", "tab.duplicate", () => _ = DuplicateTabByIdAsync(tab.Id), true, "Copy"));
        items.Add(BuildTabPlacementMenuItem());
        items.Add(Item("Move tab to new window", "tab.detach", () => _ = DetachTabAsync(tab.Id, this.PointToScreen(new Point(Bounds.Width / 2, 42)), fromMenu: true), _settings.TabDetachEnabled, "OpenWith"));
        if (tab is BrowserTabState browser)
            items.Add(Item("Open first image in folder", "tab.openFirstImage", () => _ = OpenFirstImageInFolderAsync(browser.Folder), true, "Image"));
        var tabFolder = tab switch
        {
            ImageTabState image => Path.GetDirectoryName(image.Path),
            BrowserTabState explorerTab => explorerTab.Folder,
            _ => null
        };
        items.Add(Item("New Explorer tab (browse folders)", "tab.newExplorer", () => _ = OpenExplorerTabAsync(tabFolder), true, "Browser"));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Reopen closed tab", "tab.restore", () => _ = RestoreClosedTabAsync(), _workspace.ClosedCount > 0, "History"));
        items.Add(Item("Close tab", "tab.close", () => _ = CloseTabByIdAsync(tab.Id), true, "Close"));
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }

    private async Task DuplicateTabByIdAsync(Guid id)
    {
        if (!_workspace.Select(id)) return;
        if (_workspace.DuplicateActive() is not null) await ActivateWorkspaceAsync();
    }

    private Task CloseTabByIdAsync(Guid id) => CloseTabWithPolicyAsync(id, "command");

    private async Task CloseTabWithPolicyAsync(Guid id, string source)
    {
        if (_workspace.Tabs.Count == 1 && string.Equals(_settings.LastTabCloseBehavior, "Keep last tab open", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Write("tabs", "last_tab_close_refused", new { id, source });
            return;
        }
        var closedIndex = _workspace.Tabs.ToList().FindIndex(t => t.Id == id);
        var closedTab = closedIndex >= 0 ? _workspace.Tabs[closedIndex] : null;
        var closedWasActive = _workspace.Active?.Id == id;
        var activation = TabCloseActivationMode;
        SnapshotTabForClosedHistory(id);
        closedTab = _workspace.Tabs.FirstOrDefault(t => t.Id == id) ?? closedTab;
        if (!_workspace.Close(id, ensureHome: false, activation, RecentlyUsedTabs())) return;
        _diagnostics.Write("tabs", "closed", new { id, source, remaining = _workspace.Tabs.Count });
        // 5.1.0 "Custom…": a new tab of the chosen kind takes the closed active tab's place (only while other tabs remain;
        // closing the last tab keeps the last-tab behaviour). If it cannot be made, the previously used tab stays active.
        if (activation == Glide.Core.Workspace.TabCloseActivationMode.Custom && closedWasActive && closedTab is not null && _workspace.Tabs.Count > 0 &&
            Glide.Core.Workspace.TabCloseCustomTargets.Create(_settings.AfterTabCloseCustomTarget, closedTab, LastExplorerFolderForNewTab(), Directory.Exists) is { } replacement)
        {
            _workspace.InsertTransferred(replacement, closedIndex, activate: true);
            RebuildTabStrip();
        }
        if (_workspace.Tabs.Count == 0)
        {
            if (string.Equals(_settings.LastTabCloseBehavior, "Close program", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            // Closing the final tab returns to a real Home destination. The Home page mode decides
            // whether that destination is the Welcome page, the configured Browser page, or the
            // recent-pictures landing page; the default is the Welcome page.
            CreateConfiguredHomeDestination();
            RebuildTabStrip();
            await ActivateWorkspaceAsync();
            _diagnostics.Write("tabs", "last_tab_fallback_home", new { homePage = _settings.HomePageMode });
            return;
        }
        await ActivateWorkspaceAsync();
    }

    private bool HomePageIsExplorer =>
        string.Equals(_settings.HomePageMode, "Explorer page", StringComparison.OrdinalIgnoreCase);

    private TabState CreateConfiguredHomeDestination()
    {
        // "Explorer page" (4.8.0) lands on Glide's built-in folder browser. Legacy "Browser page"
        // values from before 4.7.0 stay treated as Welcome/Home, as 4.7.0 migrated them.
        return HomePageIsExplorer ? _workspace.AddBrowser(ResolveHomeBrowserFolder()) : _workspace.AddHome();
    }

    private void ApplyConfiguredStartupWorkspace()
    {
        var action = string.IsNullOrWhiteSpace(_settings.StartupAction) ? "Welcome tab" : _settings.StartupAction;
        if (string.Equals(action, "Open last session", StringComparison.OrdinalIgnoreCase))
        {
            var restored = WorkspaceSessionStore.Load();
            if (restored is { } session)
            {
                // Explorer tabs round-trip again (4.8.0); a missing folder falls back at activation.
                var restoredTabs = session.Tabs.ToArray();
                _workspace.Reset(restoredTabs, Math.Clamp(session.ActiveIndex, 0, Math.Max(0, restoredTabs.Length - 1)));
                _diagnostics.Write("workspace", "startup_session_restored", new { tabs = restoredTabs.Length, session.ActiveIndex, explorerTabs = restoredTabs.Count(t => t is BrowserTabState) });
                return;
            }
            action = "Welcome tab";
        }

        TabState initial = new HomeTabState(Guid.NewGuid());
        string? notice = null;
        if (string.Equals(action, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            // 5.0.0 fix: a custom FOLDER opens as an Explorer tab and a custom image FILE opens as an
            // image tab. Before, only files were honoured and a folder silently fell back to Welcome.
            var target = CustomLaunchTarget.Resolve(_settings.StartupCustomPath, "Startup custom");
            switch (target.Kind)
            {
                case CustomLaunchKind.Image: initial = new ImageTabState(Guid.NewGuid(), target.Path); break;
                case CustomLaunchKind.Folder: initial = new BrowserTabState(Guid.NewGuid(), target.Path); break;
                default: notice = target.Notice; break;
            }
            _diagnostics.Write("launch", "custom_startup_destination", new { kind = target.Kind.ToString(), target.Path, fellBack = !target.IsUsable });
        }
        _workspace.Reset(new[] { initial }, 0);
        if (notice is not null) _tabNotices[initial.Id] = notice;
    }

    /// <summary>Why a Home tab opened instead of the configured custom destination (shown on the Welcome page).</summary>
    private readonly Dictionary<Guid, string> _tabNotices = new();

    private TabState CreateConfiguredNewTabDestination()
    {
        var action = string.IsNullOrWhiteSpace(_settings.NewTabAction) ? "Welcome tab" : _settings.NewTabAction;
        if (string.Equals(action, "Explorer tab", StringComparison.OrdinalIgnoreCase))
        {
            // Precedence for the folder of a new Explorer tab opened by "New tab opens: Explorer tab":
            //   1. the New-tab custom path when one is set and usable (a folder, or the folder of an image file);
            //   2. the "New Explorer tab default directory" setting;
            //   3. the last Explorer-tab location (when "use last location" is on);
            //   4. Pictures, then the user profile.
            // A custom path that is set but missing/unreadable falls back to 2-4 with a short message.
            if (string.IsNullOrWhiteSpace(_settings.NewTabCustomPath)) return AddNewBrowserTab(ResolveFreshExplorerFolder());
            var custom = CustomLaunchTarget.Resolve(_settings.NewTabCustomPath, "New-tab custom");
            var customFolder = custom.Kind switch
            {
                CustomLaunchKind.Folder => custom.Path,
                CustomLaunchKind.Image => Path.GetDirectoryName(custom.Path),
                _ => null
            };
            if (!string.IsNullOrEmpty(customFolder) && Directory.Exists(customFolder)) return AddNewBrowserTab(customFolder);
            var fallback = AddNewBrowserTab(ResolveFreshExplorerFolder());
            if (custom.Notice is not null) _tabNotices[fallback.Id] = custom.Notice + "  Opened the usual start folder instead.";
            return fallback;
        }
        if (string.Equals(action, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var target = CustomLaunchTarget.Resolve(_settings.NewTabCustomPath, "New-tab custom");
            switch (target.Kind)
            {
                case CustomLaunchKind.Image: return AddNewImageTab(target.Path);
                case CustomLaunchKind.Folder: return AddNewBrowserTab(target.Path);
                default:
                    var home = AddNewHomeTab();
                    if (target.Notice is not null) _tabNotices[home.Id] = target.Notice;
                    return home;
            }
        }
        return AddNewHomeTab();
    }

    /// <summary>The folder a "new empty Explorer tab" opens at: the last folder new Explorer tabs used, else the usual fresh folder.</summary>
    private string LastExplorerFolderForNewTab() =>
        !string.IsNullOrWhiteSpace(_settings.LastNewExplorerTabDirectory) && Directory.Exists(_settings.LastNewExplorerTabDirectory)
            ? Path.GetFullPath(_settings.LastNewExplorerTabDirectory)
            : ResolveFreshExplorerFolder();

    private string ResolveFreshExplorerFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.NewExplorerTabDefaultDirectory) && Directory.Exists(_settings.NewExplorerTabDefaultDirectory))
            return Path.GetFullPath(_settings.NewExplorerTabDefaultDirectory);
        if (_settings.NewExplorerTabsUseLastLocation && Directory.Exists(_settings.LastNewExplorerTabDirectory))
            return Path.GetFullPath(_settings.LastNewExplorerTabDirectory);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)) return pictures;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private string ResolveHomeBrowserFolder()
    {
        if (_settings.RememberLastOpenLocation && Directory.Exists(_settings.LastOpenDirectory)) return _settings.LastOpenDirectory;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)) return pictures;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    /// <summary>The "+" button's right-click menu: a configured new tab, Welcome, or an Explorer tab.</summary>
    private ContextMenu BuildNewTabMenu()
    {
        MenuItem Item(string label, string actionId, string icon)
        {
            var action = AppActionRegistry.Find(actionId)!;
            var item = MenuActions.Tag(new MenuItem { Header = MenuActions.Header(label, ShortcutForAction(actionId)), Icon = MenuIcon(icon) }, actionId);
            item.Click += async (_, _) => await ExecuteActionAsync(action, "menu");
            return item;
        }
        return PrepareOwnedContextMenu(new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("New tab", "tab.new", "Plus"),
                Item("New Welcome tab", "tab.newHome", "Home"),
                Item("New Explorer tab (browse folders)", "tab.newExplorer", "Browser")
            }
        });
    }

    /// <summary>Opens a new Explorer tab (Glide's built-in folder browser) and activates it.</summary>
    private async Task OpenExplorerTabAsync(string? folder = null)
    {
        folder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : ResolveFreshExplorerFolder();
        AddNewBrowserTab(folder);
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
    }

    private async Task OpenFirstImageInFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string? first = null;
        try { first = Directory.EnumerateFiles(folder).Where(ImageNavigator.IsSupported).OrderBy(p => p, Glide.Core.ImageSortOrder.CreateComparer()).FirstOrDefault(); } catch { /* unreadable folder: no first image is chosen */ }
        if (first is not null) await OpenAsImageTabAsync(first);
    }

    private async void TabCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Guid id }) return;
        await CloseTabWithPolicyAsync(id, "close_button");
    }

    // The dormant right-side TabNav buttons of MainWindow.axaml (hidden since the built-in title navigation was removed)
    // used to keep their own "Up to the containing folder / Forward to the image" state. 5.0.0 replaces that with the one
    // per-tab history (MainWindow.History.cs); the buttons, if ever shown again, are plain Back / Forward.
    private async void TabNavBackClicked(object? sender, RoutedEventArgs e)
    {
        if (!_titleNavCanBack) return;
        await NavigateBackAsync();
    }

    private async void TabNavForwardClicked(object? sender, RoutedEventArgs e)
    {
        if (!_titleNavCanForward) return;
        await NavigateForwardAsync();
    }

    private void ApplyTabUtilityButtonVisibility()
    {
        // Built-in Explorer/title navigation was removed.
        TabNavigationHost.IsVisible = false;
        TabNavBackButton.IsVisible = false;
        TabNavForwardButton.IsVisible = false;
        TabNavBackButton.ContextMenu = null;
        TabNavForwardButton.ContextMenu = null;
        UpdateTabNavigationButtons();
    }

    private void UpdateTabNavigationButtons()
    {
        // Every Back / Forward control (title-bar buttons, Explorer toolbar) follows the active tab's history.
        UpdateHistoryNavigationState();
        UpdatePictureAddressRow();
        if (TabNavBackButton is null || TabNavForwardButton is null) return;
        var canBack = Glide.Core.Navigation.CrossTabHistory.CanGo(ActiveHistory, -1, ExplorerOptionsView.BackReturnsToImage);
        var canForward = Glide.Core.Navigation.CrossTabHistory.CanGo(ActiveHistory, 1, ExplorerOptionsView.BackReturnsToImage);
        _titleNavCanBack = canBack;
        _titleNavCanForward = canForward;
        TabNavBackButton.Classes.Set("inactive", !canBack);
        TabNavForwardButton.Classes.Set("inactive", !canForward);
        TabNavBackButton.Opacity = canBack ? 1.0 : 0.34;
        TabNavForwardButton.Opacity = canForward ? 1.0 : 0.34;
        TabNavBackButton.IsEnabled = false;
        TabNavForwardButton.IsEnabled = false;
        TabNavBackButton.IsVisible = false;
        TabNavForwardButton.IsVisible = false;
        TabNavigationHost.IsVisible = false;
    }

    private void SelectBrowserHighlight(Guid tabId)
    {
        // A selection carried over a refresh / tab switch wins over the remembered highlight.
        if (_browserSelection.Count > 0) return;
        var target = CurrentBrowserHistoryHighlight(tabId);
        if (string.IsNullOrWhiteSpace(target))
            _browserHighlightTargets.TryGetValue(tabId, out target);
        if (string.IsNullOrWhiteSpace(target)) return;
        var index = _browserEntries.FindIndex(entry => string.Equals(entry.Path, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _browserSelection.Clear();
        _browserSelection.Add(target);
        _browserSelectionAnchor = index;
        _browserCurrentIndex = index;
        UpdateRealizedBrowserSelection();
        EnsureExplorerIndexVisible(index);
    }

    private async void ChromeMiddleClickReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;

        // A tab middle-click closes on press. Closing rebuilds the strip before release, so using
        // only the release-time visual source can misclassify that same gesture as empty chrome
        // and immediately create a replacement tab. Preserve press ownership across the rebuild:
        // new-tab creation is legal only when the gesture both started and ended on empty chrome.
        var startedOnEmptyChrome = _chromeMiddlePressStartedOnEmptyChrome && ReferenceEquals(_chromeMiddlePressPointer, e.Pointer);
        var pressPosition = _chromeMiddlePressPosition;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        _chromeMiddlePressPointer = null;

        if (!startedOnEmptyChrome) return;
        if (e.Source is Control source && IsChromeInteractiveSource(source)) return;

        var releasePosition = e.GetPosition(ChromeBorder);
        const double clickMovementTolerance = 6.0;
        if (Math.Abs(releasePosition.X - pressPosition.X) > clickMovementTolerance ||
            Math.Abs(releasePosition.Y - pressPosition.Y) > clickMovementTolerance)
            return;

        // Title bar middle-click row: Default opens a new tab (unchanged); another action replaces it.
        var configuredMiddle = ResolveGestureSlot("titleBar.middleClick", e.KeyModifiers);
        if (!string.Equals(configuredMiddle, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            await ExecuteGestureActionAsync(configuredMiddle);
            return;
        }
        var tab = CreateConfiguredNewTabDestination();
        _diagnostics.Write("tabs", "middle_click_new_tab", new { tab.Id, tab.Kind });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        e.Handled = true;
    }

    private static bool IsChromeInteractiveSource(Control source)
    {
        for (Control? current = source; current is not null; current = current.GetVisualParent() as Control)
        {
            if (current is Button || current.Classes.Contains("glideTab")) return true;
        }
        return false;
    }

    private async void NewTabClicked(object? sender, RoutedEventArgs e)
    {
        var tab = CreateConfiguredNewTabDestination();
        _diagnostics.Write("tabs", "new_tab_destination", new { tab.Id, tab.Kind, newTabAction = _settings.NewTabAction, count = _workspace.Tabs.Count });
        await ActivateWorkspaceAsync();
    }

    private void RefreshRecentHistoryHome()
    {
        if (_homeSurface is null) return;
        _homeSurface.RecentHost.Children.Clear();
        if (!_settings.RecentHistoryEnabled ||
            (!_settings.ShowRecentOnHome && !string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase)))
        {
            _homeSurface.RecentPanel.IsVisible = false;
            _homeSurface.EmptyText.Text = !_settings.RecentHistoryEnabled
                ? "Recent history is turned off (Settings → General)."
                : "Recent is hidden on Home (Settings → General → Show Recent on Home).";
            _homeSurface.EmptyState.IsVisible = true;
            return;
        }

        var entries = RecentHistoryStore.Snapshot(_settings.HistorySize);
        if (string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase))
            entries = entries.Where(entry => !string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var entry in entries)
        {
            var button = new Button
            {
                Tag = entry,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                // A TextBlock, not a string: a string Content becomes AccessText, which eats "_" in file names as a mnemonic.
                Content = new TextBlock { Text = $"{(string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase) ? "Folder" : "Image")}   {entry.Path}" }
            };
            button.Classes.Add("recentItem");
            ToolTip.SetTip(button, entry.Path);
            button.Click += RecentHistoryItemClicked;
            button.AddHandler(InputElement.PointerReleasedEvent, RecentHistoryItemMiddleReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _homeSurface.RecentHost.Children.Add(button);
        }
        _homeSurface.RecentPanel.IsVisible = entries.Count > 0;
        _homeSurface.EmptyText.Text = "No recent pictures yet.";
        _homeSurface.EmptyState.IsVisible = entries.Count == 0;
    }

    private async void RecentHistoryItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentHistoryStore.Entry entry }) return;
        if (string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(entry.Path)) { RefreshRecentHistoryHome(); return; }
            _workspace.AddBrowser(entry.Path);
            RecentHistoryStore.RecordFolder(entry.Path, _settings.HistorySize);
            await ActivateWorkspaceAsync();
            return;
        }
        if (File.Exists(entry.Path)) await OpenAsImageTabAsync(entry.Path);
        else RefreshRecentHistoryHome();
    }

    /// <summary>5.1.0: middle-click on a Recent row opens it in a new tab with the Explorer middle-click policy (default: background, at the end; held keys flip it).</summary>
    private async void RecentHistoryItemMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || sender is not Button { Tag: RecentHistoryStore.Entry entry }) return;
        e.Handled = true;
        var opening = ResolveMiddleClickOpening(e.KeyModifiers);
        if (string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(entry.Path)) { RefreshRecentHistoryHome(); return; }
            await OpenFolderInNewTabAsync(entry.Path, opening, "home_recent_middle_click");
            RecentHistoryStore.RecordFolder(entry.Path, _settings.HistorySize);
            return;
        }
        if (File.Exists(entry.Path)) await OpenImageInNewTabAsync(entry.Path, opening, "home_recent_middle_click");
        else RefreshRecentHistoryHome();
    }

    private void ClearRecentHistoryClicked(object? sender, RoutedEventArgs e) => ClearRecentHistoryList();

    private void ClearRecentHistoryList()
    {
        RecentHistoryStore.Clear();
        RefreshRecentHistoryHome();
        _diagnostics.Write("history", "cleared");
    }

    private void CloseRecentHistoryClicked(object? sender, RoutedEventArgs e)
    {
        _settings.ShowRecentOnHome = false;
        SaveSettingsAndPublish();
        RefreshRecentHistoryHome();
    }

    private void ResizeRecentHistoryClicked(object? sender, RoutedEventArgs e) => CycleRecentHistoryTileSize();

    private void CycleRecentHistoryTileSize()
    {
        _settings.WelcomeRecentSize = _settings.WelcomeRecentSize switch
        {
            "Compact" => "Large",
            "Large" => "Normal",
            _ => "Compact"
        };
        SaveSettingsAndPublish();
        ApplyWelcomeLayout();
    }

    private void ApplyWelcomeLayout()
    {
        if (_homeSurface is null) return;
        var size = string.IsNullOrWhiteSpace(_settings.WelcomeRecentSize) ? "Normal" : _settings.WelcomeRecentSize;
        _homeSurface.RecentScroll.MaxHeight = size switch { "Compact" => 150, "Large" => 420, _ => 10000 };
        _homeSurface.RecentResize.Kind = string.Equals(size, "Large", StringComparison.OrdinalIgnoreCase) ? "Collapse" : "Expand";
        ToolTip.SetTip(_homeSurface.RecentResizeButtonControl, $"Resize Recent. Current size: {size}. Click to cycle Normal → Compact → Large.");
    }
    private Task CloseActiveTabAsync()
    {
        var active = _workspace.Active;
        return active is null ? Task.CompletedTask : CloseTabWithPolicyAsync(active.Id, "active_command");
    }

    /// <summary>
    /// 5.1.6: before a tab closes, commit its live state (image zoom/pan, Explorer folder, selection, scroll, folder
    /// history) into its record and attach its Back / Forward history, so Reopen closed tab brings it back as it was.
    /// </summary>
    private void SnapshotTabForClosedHistory(Guid id)
    {
        try
        {
            if (_workspace.Active?.Id == id && _workspace.Active is { } active) SyncCurrentHistoryEntry(active);
            CaptureTabRuntimeState(id);
            if (_workspace.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab) return;
            if (_tabHistories.TryGet(id, out var history) && history.Count > 0)
                _workspace.ReplaceTab(tab with { History = history.Snapshot() });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("tabs", "close_snapshot_failed", new { id, error = ex.Message });
        }
    }

    private async Task RestoreClosedTabAsync()
    {
        if (_workspace.RestoreClosed() is not { } restored) return;
        // The tab's history travels in its record: drop any stale live copy, adopt it, then clear the travelling copy.
        _tabHistories.Take(restored.Id);
        _tabHistories.For(restored);
        _workspace.ReplaceTab(restored with { History = null });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
    }

    private void CopyWindowGeometryTo(MainWindow child)
    {
        if (WindowState == WindowState.Normal)
        {
            child.Width = Math.Max(child.MinWidth, Bounds.Width);
            child.Height = Math.Max(child.MinHeight, Bounds.Height);
            child.Position = Position;
        }
        else if (_normalPlacementKnown)
        {
            child.Width = Math.Max(child.MinWidth, _lastNormalWindowSize.Width);
            child.Height = Math.Max(child.MinHeight, _lastNormalWindowSize.Height);
            child.Position = _lastNormalWindowPosition;
        }
        else
        {
            child.Width = Math.Max(child.MinWidth, Bounds.Width);
            child.Height = Math.Max(child.MinHeight, Bounds.Height);
        }

        if (WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _stateBeforeMinimize == WindowState.Maximized))
            child.WindowState = WindowState.Maximized;
    }

    private static TabState CloneTabForIndependentWindow(TabState tab) => tab switch
    {
        ImageTabState image => new ImageTabState(Guid.NewGuid(), image.Path) { ViewState = image.ViewState },
        BrowserTabState browser => new BrowserTabState(Guid.NewGuid(), browser.Folder) { Navigation = browser.Navigation, View = browser.View },
        _ => new HomeTabState(Guid.NewGuid())
    };

    private async Task OpenCurrentInNewWindowAsync()
    {
        CaptureActiveTabRuntimeState();
        var source = _workspace.Active;
        if (source is null) return;
        // Ctrl+N means a new independent Glide window focused on the same current content. For image
        // tabs this is exactly the same picture/view; Home/Explorer mirror their current destination.
        var cloned = CloneTabForIndependentWindow(source);
        var child = new MainWindow(cloned, _settings.CloneState(), deferWorkspaceActivation: true);
        CopyWindowGeometryTo(child);
        ShowSecondaryWindow(child);
        await child.ActivateWorkspaceAsync();
        child.Activate();
    }

    private async Task DuplicateSessionWindowAsync()
    {
        CaptureActiveTabRuntimeState();
        var activeIndex = Math.Max(0, _workspace.ActiveIndex);
        var clones = _workspace.Tabs.Select(CloneTabForIndependentWindow).ToArray();
        if (clones.Length == 0) return;
        var child = new MainWindow(clones[0], _settings.CloneState(), deferWorkspaceActivation: true);
        child._workspace.Reset(clones, Math.Clamp(activeIndex, 0, clones.Length - 1));
        CopyWindowGeometryTo(child);
        ShowSecondaryWindow(child);
        await child.ActivateWorkspaceAsync();
        child.Activate();
    }

    private async Task DuplicateActiveTabAsync()
    {
        if (_workspace.DuplicateActive() is not null) await ActivateWorkspaceAsync();
    }

    private async Task CycleTabAsync(int delta)
    {
        if (!TabCycleOrders.IsMostRecentlyUsed(_settings.TabCycleOrder) || _workspace.Tabs.Count < 2)
        {
            if (_workspace.SelectRelative(delta)) await ActivateWorkspaceAsync();
            return;
        }

        // Without Ctrl held (a rebound shortcut or menu), each press is one complete MRU switch.
        var holdingCtrl = _lastKeyDownHadCtrl;
        var target = _tabStrip.NextInCycle(TabIds, _workspace.Active?.Id, delta, holdingCtrl);
        if (_workspace.Select(target)) await ActivateWorkspaceAsync();
        if (!holdingCtrl) TouchTabMru();
    }

    private async Task SelectTabIndexAsync(int index)
    {
        if (_workspace.Tabs.Count == 0) return;
        index = Math.Clamp(index, 0, _workspace.Tabs.Count - 1);
        if (_workspace.Select(_workspace.Tabs[index].Id)) await ActivateWorkspaceAsync();
    }

    private async Task HandleEscapeAsync()
    {
        // Mature Glide Escape hierarchy: stop/restore an active slideshow session first, then leave
        // fullscreen, then clear an ordinary windowed selection, then apply optional close policy.
        if (_slideshow?.IsActive == true && _settings.EscStopsSlideshow)
        {
            StopSlideshow(restoreStartingMode: true);
            return;
        }
        if (WindowState == WindowState.FullScreen && _settings.EscExitsFullscreen)
        {
            ToggleFullscreen();
            return;
        }
        if (Viewport.ClearSelection()) return;
        if (WindowState != WindowState.FullScreen && _settings.EscWindowedConfirm)
        {
            if (string.Equals(_settings.EscWindowedRememberedChoice, "Close", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            if (string.Equals(_settings.EscWindowedRememberedChoice, "KeepOpen", StringComparison.OrdinalIgnoreCase)) return;

            var choice = await ConfirmCloseAsync();
            if (choice.Remember)
            {
                _settings.EscWindowedRememberedChoice = choice.Close ? "Close" : "KeepOpen";
                SaveSettingsAndPublish();
            }
            if (choice.Close) Close();
        }
    }

    /// <summary>
    /// Windows couldn't play a video. Glide uses only the codecs installed in Windows, so it says exactly which one is
    /// missing (read from the file's container and checked against Windows' decoders); finding it is up to the user.
    /// </summary>
    private async Task ShowVideoUnavailableAsync(string path, string? reason)
    {
        if (!IsVisible) return;
        var diagnosis = await Task.Run(() => VideoPlaybackDiagnoser.Diagnose(path));
        _diagnostics.Write("video", "unavailable_diagnosis", new
        {
            reason,
            kind = diagnosis.Kind.ToString(),
            missing = string.Join(",", diagnosis.MissingCodecs.Select(x => x.Id)),
        });
        if (!IsVisible) return;
        var dialog = CreateContentSizedDialog("Can't play this video", 380, 560);
        PopupPlacementStore.Track(dialog, "overlay-video-unavailable");
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 13,
            Children =
            {
                new TextBlock { Text = "Can't play this video", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap, Opacity = .8 },
                new TextBlock { Text = diagnosis.Message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { ok } }
            }
        };
        await ShowOwnedDialogAsync(dialog);
    }

    private async Task<(bool Close, bool Remember)> ConfirmCloseAsync()
    {
        var closeRequested = false;
        var decided = false;
        var dialog = CreateContentSizedDialog("Close Glide?", 380, 520);
        PopupPlacementStore.Track(dialog, "escape-close-confirm");
        var remember = new CheckBox { Content = "Remember my choice" };
        var yes = new Button { Content = "Close", MinWidth = 90 };
        var no = new Button { Content = "Keep open", MinWidth = 90 };
        yes.Click += (_, _) => { closeRequested = true; decided = true; dialog.Close(); };
        no.Click += (_, _) => { closeRequested = false; decided = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 13,
            Children =
            {
                new TextBlock { Text = "Close Glide?", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Escape is configured to ask before closing the window.", TextWrapping = TextWrapping.Wrap },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        await ShowOwnedDialogAsync(dialog);
        return decided ? (closeRequested, remember.IsChecked == true) : (false, false);
    }
}
