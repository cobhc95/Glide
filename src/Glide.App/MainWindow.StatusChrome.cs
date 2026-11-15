using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.Core;
using Glide.App.Services;
using Glide.Core.Commands;

namespace Glide.App;

/// <summary>Status visibility, caption buttons, pointer/drag handling and fullscreen. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private void ApplyStatusVisibility()
    {
        if (_wholeAppOverlayMode)
        {
            ViewerStatusSurface.IsVisible = false;
            if (_homeSurface is not null) _homeSurface.StatusSurface.IsVisible = false;
            StatusRestoreButton.IsVisible = false;
            return;
        }
        // Turning Auto-hide on or off always starts from a clean state (hidden / normal), never a stale reveal.
        if (_statusAutoHideApplied != _settings.StatusAutoHide)
        {
            _statusAutoHideApplied = _settings.StatusAutoHide;
            _statusHoverRevealed = false;
            _statusHoverCollapsedRect = null;
        }
        var collapsedNow = StatusCollapsedNow;
        if (!collapsedNow && !_settings.StatusAutoHide) _statusHoverRevealed = false;
        var fullscreen = WindowState == WindowState.FullScreen;
        // Auto-hide also covers fullscreen: the bar is hidden anyway and only appears at the bottom edge.
        var allowed = _settings.ShowStatusSurface && (!fullscreen || _settings.FullscreenStatusAlwaysOn || _settings.StatusAutoHide);
        var expanded = allowed && !collapsedNow && !_statusSessionClosed;
        // Windowed collapse keeps the bar present but collapsed to its own button: that same
        // button turns into the expand arrow (UpdateCollapseButtonGlyphs) and clicking it again
        // expands the bar. No separate restore arrow is left in the corner, and collapse never
        // closes the bar (the X / CloseStatusClicked is the only close path). The corner button
        // remains only for fullscreen, where the bar is suppressed rather than collapsed.
        var collapsedOnly = _settings.ShowStatusSurface && collapsedNow && !_statusSessionClosed && !fullscreen;
        // Auto-hide: the bar is fully hidden (no arrow, no button) until UpdateStatusHoverReveal reveals it.
        var barVisible = StatusBarAutoHide.BarVisible(_settings.StatusAutoHide, _statusHoverRevealed);
        ViewerStatusSurface.IsVisible = (expanded || collapsedOnly) && ImageView.IsVisible && barVisible;
        if (_homeSurface is not null) _homeSurface.StatusSurface.IsVisible = (expanded || collapsedOnly) && HomeHost.IsVisible && (!fullscreen || _settings.StatusAutoHide) && barVisible;
        StatusRestoreButton.IsVisible = _settings.ShowStatusSurface && fullscreen && !_settings.StatusAutoHide &&
            (collapsedNow || !_settings.FullscreenStatusAlwaysOn) &&
            (ImageView.IsVisible || HomeHost.IsVisible);
        UpdateCollapseButtonGlyphs();
        ApplyCollapsedStatusChrome();
        UpdateViewportStatusInset();
    }

    /// <summary>
    /// Collapsed means "only the collapse/expand button remains", leaving that single button (now
    /// showing the expand arrow) where the user can click it again. Two placements:
    /// <list type="bullet">
    /// <item>Corner on (default): the bar shrinks to just the button, parked lower-right.</item>
    /// <item>Corner off: the full expanded bar stays laid out (so the adaptive layout keeps
    /// computing the collapse button's true live position, including 2-3 rows, resizes, Left /
    /// Center / Right and DPI changes) but everything except the collapse button is transparent
    /// and click-through, as is the surface itself. The button therefore never moves.</item>
    /// </list>
    /// Hover reveal temporarily shows the full bar again without clearing the collapsed state.
    /// </summary>
    private void ApplyCollapsedStatusChrome()
    {
        if (!_statusProbing) _statusProbeDirty = true;       // the bar's box (reveal area) is re-measured after any state change
        var collapsedOnly = !_statusPreviewReveal && _settings.ShowStatusSurface && StatusCollapsedNow && !_statusHoverRevealed &&
            !_statusSessionClosed && WindowState != WindowState.FullScreen && !_wholeAppOverlayMode;
        var compact = collapsedOnly && _settings.StatusCollapsedToCorner;
        var ghost = collapsedOnly && !_settings.StatusCollapsedToCorner;
        if (StatusControlsPanel is not null) { StatusControlsPanel.IsVisible = !compact; SetGhosted(StatusControlsPanel, ghost); }
        if (StatusStatsHost is not null) { StatusStatsHost.IsVisible = !compact && Bounds.Width >= 260; SetGhosted(StatusStatsHost, ghost); }
        if (StatusCloseButton is not null) { StatusCloseButton.IsVisible = !compact && _settings.StatusShowClose; SetGhosted(StatusCloseButton, ghost); }
        // Corner mode: the collapsed bar is only the button, no empty strip, in the lower-right.
        if (compact) PinCollapsedSurfaceToCorner(ViewerStatusSurface);
        else ApplyStatusSurfaceAlignment(ViewerStatusSurface);
        SetStatusSurfaceChromeless(ViewerStatusSurface, collapsedOnly);
        ApplyCollapsedButtonLook(collapsedOnly);
        // the minimise button stays on a revealed auto-hide bar, with its own auto-hide glyph; right-click toggles auto-hide.
        if (StatusCollapseButton is not null) StatusCollapseButton.IsVisible = true;
        if (_homeSurface is not null)
        {
            _homeSurface.StatusText.IsVisible = !compact;
            SetGhosted(_homeSurface.StatusText, ghost);
            _homeSurface.StatusControlsPanel.IsVisible = !compact;
            foreach (var button in _homeSurface.StatusControlsPanel.Children.OfType<Button>())
            {
                button.IsVisible = !compact;
                SetGhosted(button, ghost);
            }
            foreach (var button in _homeSurface.StatusWindowControls.Children.OfType<Button>())
            {
                var isCollapse = ReferenceEquals(button, _homeSurface.StatusCollapseButton);
                button.IsVisible = !compact || isCollapse;
                SetGhosted(button, ghost && !isCollapse);
            }
            _homeSurface.StatusCollapseButton.IsVisible = true;
            if (compact) PinCollapsedSurfaceToCorner(_homeSurface.StatusSurface);
            else ApplyStatusSurfaceAlignment(_homeSurface.StatusSurface);
            SetStatusSurfaceChromeless(_homeSurface.StatusSurface, collapsedOnly);
        }
    }

    /// <summary>
    /// "Collapsed button in lower-right corner" OFF: the expand button stays where the collapse button was and rests
    /// as a very small, unobtrusive arrow. The full framed button and shade appear only while it is hovered (the
    /// pointer-over look is a style on the same class, so it needs no pointer tracking here).
    /// </summary>
    private void ApplyCollapsedButtonLook(bool collapsedOnly)
    {
        var look = StatusBarAutoHide.ExpandButtonLook(collapsedOnly, _settings.StatusCollapsedToCorner, _settings.StatusAutoHide, pointerOverButton: false);
        var arrow = look == StatusButtonLook.Arrow;
        SetCollapsedArrowClass(StatusCollapseButton, arrow);
        SetCollapsedArrowClass(_homeSurface?.StatusCollapseButton, arrow);
    }

    private static void SetCollapsedArrowClass(Button? button, bool arrow)
    {
        if (button is null) return;
        if (arrow) { if (!button.Classes.Contains("collapsedArrow")) button.Classes.Add("collapsedArrow"); }
        else button.Classes.Remove("collapsedArrow");
    }

    /// <summary>Whether the collapse flag is honoured: Auto-hide replaces collapsing (the bar is simply hidden until revealed).</summary>
    private bool StatusCollapsedNow => _statusCollapsed && !_settings.StatusAutoHide;

    private bool _statusAutoHideApplied;

    /// <summary>Keeps a control in layout but makes it invisible and click-through (or restores it).</summary>
    private static void SetGhosted(Control control, bool ghosted)
    {
        if (ghosted)
        {
            control.Opacity = 0;
            control.IsHitTestVisible = false;
        }
        else
        {
            control.ClearValue(Visual.OpacityProperty);
            control.ClearValue(InputElement.IsHitTestVisibleProperty);
        }
    }

    /// <summary>
    /// A collapsed bar shows only its expand button: no panel fill, border or shadow around it. A null
    /// background (not Transparent) keeps the empty area of a ghosted full-size bar click-through.
    /// </summary>
    private static void SetStatusSurfaceChromeless(Border surface, bool chromeless)
    {
        if (chromeless)
        {
            surface.Background = null;
            surface.BorderBrush = null;
            surface.BoxShadow = default;
        }
        else
        {
            surface.ClearValue(Border.BackgroundProperty);
            surface.ClearValue(Border.BorderBrushProperty);
            surface.ClearValue(Border.BoxShadowProperty);
        }
    }

    /// <summary>Left / Center / Right placement of the expanded status bar (user choice, right-click menu or Settings).</summary>
    private void ApplyStatusSurfaceAlignment(Border surface)
    {
        surface.Width = double.NaN;
        surface.Margin = new Thickness(14, 0, 14, 14);
        surface.HorizontalAlignment = _settings.StatusBarAlignment switch
        {
            "Left" => HorizontalAlignment.Left,
            "Right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
    }

    private static void PinCollapsedSurfaceToCorner(Border surface)
    {
        surface.Width = double.NaN;
        surface.HorizontalAlignment = HorizontalAlignment.Right;
        surface.Margin = new Thickness(14, 0, 14, 14);
    }

    private void UpdateCollapseButtonGlyphs()
    {
        void Apply(Button? button)
        {
            if (button?.Content is not GlideIconView glyph) return;
            if (_settings.StatusAutoHide && !StatusCollapsedNow)
            {
                // distinct glyph while auto-hide is on.
                glyph.Kind = "Eye";
                ToolTip.SetTip(button, "Auto-hide is on: the status bar hides until the pointer reaches it. Click or right-click to keep it shown.");
                return;
            }
            glyph.Kind = StatusCollapsedNow ? "Expand" : "Collapse";
            ToolTip.SetTip(button, StatusCollapsedNow
                ? $"Expand status bar ({ShortcutFor(GlideCommand.ToggleStatusSurface)})"
                : $"Collapse status bar ({ShortcutFor(GlideCommand.ToggleStatusSurface)}). Right-click: auto-hide the status bar");
        }
        Apply(StatusCollapseButton);
        Apply(_homeSurface?.StatusCollapseButton);
    }

    /// <param name="state">
    /// The state to lay the chrome out for; defaults to the current one. Fullscreen enter/exit pass the target
    /// state before the native change, so the synchronous WM_SIZE layout pass already uses the final chrome.
    /// </param>
    private void ApplyChromeLayoutForWindowState(WindowState? state = null)
    {
        if (MainRoot.RowDefinitions.Count < 2) return;
        ApplyTabPosition();

        if (_wholeAppOverlayMode)
        {
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = false;
            FullscreenCornerCloseHit.IsVisible = false;
            return;
        }

        var fullscreen = (state ?? WindowState) == WindowState.FullScreen;
        if (fullscreen && _settings.FullscreenKeepTabBarOpen)
        {
            // Pinned fullscreen chrome participates in layout: the image resizes below the 44-DIP row.
            MainRoot.RowDefinitions[0].Height = new GridLength(44);
            Grid.SetRow(ChromeBorder, 0);
            ChromeBorder.ZIndex = 0;
            ChromeBorder.IsVisible = true;
            FullscreenCornerCloseHit.IsVisible = false;
        }
        else if (fullscreen)
        {
            // Default fullscreen remains edge-to-edge. Revealed chrome overlays the image and reserves no space.
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = !_settings.AutoHideFullscreenChrome;
            FullscreenCornerCloseHit.IsVisible = true;
        }
        else if (TopTabsAutoHideActive)
        {
            // windowed "Auto-hide the top tab strip": the title/tab row overlays the top of the content and
            // appears when the pointer reaches the top edge (same reveal as fullscreen chrome).
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = false;
            FullscreenCornerCloseHit.IsVisible = false;
        }
        else
        {
            MainRoot.RowDefinitions[0].Height = new GridLength(44);
            Grid.SetRow(ChromeBorder, 0);
            ChromeBorder.ZIndex = 0;
            ChromeBorder.IsVisible = true;
            FullscreenCornerCloseHit.IsVisible = false;
        }
    }

    private void UpdateIntegratedTitleBarInset()
    {
        // Glide owns the 44-DIP title row; the caption controls are a real grid column rather than
        // an approximate right-side margin. This makes the close hit target reach the top/right edge
        // and prevents tab/tool content from drifting underneath caption controls at any DPI.
        // Fullscreen auto-hide reveals the complete browser chrome, not a reduced tab-only strip.
        // Caption controls therefore remain part of the same 44-DIP row and use the exact same
        // geometry as windowed mode whenever the hot edge reveals ChromeBorder.
        CaptionButtonHost.IsVisible = true;
        ChromeLayout.Margin = new Thickness(8, 0, 0, 0);
    }

    private enum CaptionButtonRole { Minimize, Maximize, Close }

    private void UpdateCaptionButtonState()
    {
        if (CaptionMaximizeIcon is null) return;
        var restore = WindowState is WindowState.Maximized or WindowState.FullScreen;
        CaptionMaximizeIcon.Kind = restore ? "Restore" : "Maximize";
        var fullscreen = WindowState == WindowState.FullScreen;
        ToolTip.SetTip(CaptionMinimizeButton, $"Minimize button — {CaptionActionFor(CaptionButtonRole.Minimize, fullscreen)}");
        ToolTip.SetTip(CaptionMaximizeButton, $"Maximize button — {CaptionActionFor(CaptionButtonRole.Maximize, fullscreen)}");
        ToolTip.SetTip(CaptionCloseButton, $"Close button — {CaptionActionFor(CaptionButtonRole.Close, fullscreen)}");
    }

    private string CaptionActionFor(CaptionButtonRole role, bool fullscreen) => (fullscreen, role) switch
    {
        (false, CaptionButtonRole.Minimize) => _settings.CaptionWindowedMinimizeAction,
        (false, CaptionButtonRole.Maximize) => _settings.CaptionWindowedMaximizeAction,
        (false, CaptionButtonRole.Close) => _settings.CaptionWindowedCloseAction,
        (true, CaptionButtonRole.Minimize) => _settings.CaptionFullscreenMinimizeAction,
        (true, CaptionButtonRole.Maximize) => _settings.CaptionFullscreenMaximizeAction,
        (true, CaptionButtonRole.Close) => _settings.CaptionFullscreenCloseAction,
        _ => "No action"
    };

    private void CaptionMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Minimize);
        e.Handled = true;
    }

    private void CaptionMaximizeClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Maximize);
        e.Handled = true;
    }

    private void CaptionCloseClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Close);
        e.Handled = true;
    }

    private void ExecuteConfiguredCaptionAction(CaptionButtonRole role)
    {
        var fullscreen = WindowState == WindowState.FullScreen;
        ExecuteCaptionAction(CaptionActionFor(role, fullscreen));
    }

    private void ExecuteCaptionAction(string? action)
    {
        _diagnostics.Write("window", "caption_action", new
        {
            action = action ?? "<null>", state = WindowState.ToString(),
            suppressClose = Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick)
        });
        switch (action)
        {
            case "Minimize":
                // A fullscreen HWND must leave fullscreen before minimization or Windows can restore
                // it into an ambiguous fullscreen/minimized state on the next activation.
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
                if (!TrySendNativeSystemCommand(ScMinimize)) WindowState = WindowState.Minimized;
                break;
            case "Maximize / restore":
                if (WindowState == WindowState.FullScreen)
                {
                    ExitFullscreen(forceMaximized: false);
                    break;
                }
                // Hide/show and shell snapping can leave Avalonia's cached state out of sync
                // with the HWND. A stale Maximized value used to send SC_RESTORE to an
                // already-normal, half-screen window instead of maximizing it.
                var restore = TryGetNativeZoomed(out var zoomed) ? zoomed : WindowState == WindowState.Maximized;
                if (!TrySendNativeSystemCommand(restore ? ScRestore : ScMaximize))
                    WindowState = restore ? WindowState.Normal : WindowState.Maximized;
                break;
            case "Close Glide":
                if (Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick))
                {
                    _diagnostics.Write("window", "caption_close_suppressed_after_home");
                    break;
                }
                _forceCloseFromCaptionAction = true;
                // The custom caption X returns the last window to Speed Boost standby. The standby
                // path removes the render HWND while preserving the resident broker for warm opens.
                if (ShouldEnterSpeedBoostStandby()) EnterSpeedBoostStandby();
                else if (!TrySendNativeSystemCommand(ScClose)) Close();
                break;
            case "Enter fullscreen":
                if (WindowState != WindowState.FullScreen) EnterFullscreen();
                break;
            case "Exit fullscreen (restore previous placement)":
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
                break;
            case "Exit fullscreen maximized":
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: true);
                break;
            case "No action":
            default:
                break;
        }
    }

    private bool TrySendNativeSystemCommand(int command)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var platform = TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return false;
        try
        {
            SendMessageW(platform.Handle, WmSysCommand, (IntPtr)command, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    private long _lastChromePressTick;
    private PixelPoint _lastChromePressPoint;

    private void ChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var physicalButtons = PhysicalMouseButtons(point);

        // Classify middle-click ownership at press time, before a tab-origin press can close the
        // tab and rebuild the visual tree. This state is consumed by ChromeMiddleClickReleased.
        if (physicalButtons.Middle)
        {
            _chromeMiddlePressPointer = e.Pointer;
            _chromeMiddlePressPosition = e.GetPosition(ChromeBorder);
            _chromeMiddlePressStartedOnEmptyChrome = e.Source is not Control source || !IsChromeInteractiveSource(source);
            return;
        }

        if (physicalButtons.Right && !IsInteractiveSource(e.Source))
        {
            // Title bar right-click row. Only the empty title bar is affected: tabs, caption and
            // toolbar buttons are interactive sources and keep their own handling.
            var configuredRight = ResolveGestureSlot("titleBar.rightClick", e.KeyModifiers);
            if (!string.Equals(configuredRight, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                if (configuredRight == GestureCatalog.ContextMenu) BuildViewerContextMenu().Open(ChromeBorder);
                else Dispatcher.UIThread.Post(async () => await ExecuteGestureActionAsync(configuredRight), DispatcherPriority.Background);
                return;
            }
            if (WindowState == WindowState.FullScreen)
            {
                ShowFullscreenChromeContextMenu();
                e.Handled = true;
                return;
            }
            if (_wholeAppOverlayMode)
            {
                BuildWholeAppOverlayOnlyContextMenu().Open(ChromeBorder);
                e.Handled = true;
                return;
            }
            if (_settings.TabsEnabled)
            {
                // Empty title bar: "Place tabs vertically" / "Place tabs on top".
                ShowTabAreaContextMenu(ChromeBorder);
                e.Handled = true;
                return;
            }
        }

        // Treat non-interactive chrome exactly like a native Windows caption. This deliberately
        // includes the empty strip immediately after the final tab / plus button: single-drag moves
        // the window, while double-click toggles maximize/restore. Tab bodies and toolbar controls
        // remain interactive and are excluded by IsInteractiveSource.
        if (!physicalButtons.Left || IsInteractiveSource(e.Source)) return;

        // Avalonia's ClickCount is the primary signal; the timing/distance pair is a backstop for
        // when the platform fails to report the second click of a double-click.
        var pressTick = Environment.TickCount64;
        var pressPoint = this.PointToScreen(e.GetPosition(this));
        var isSecondClick = e.ClickCount >= 2 ||
            (_lastChromePressTick != 0 && pressTick - _lastChromePressTick <= 500 &&
             Math.Abs(pressPoint.X - _lastChromePressPoint.X) <= 8 && Math.Abs(pressPoint.Y - _lastChromePressPoint.Y) <= 8);
        if (isSecondClick) _lastChromePressTick = 0;
        else { _lastChromePressTick = pressTick; _lastChromePressPoint = pressPoint; }

        // Title bar double-click row. Default keeps the native-style maximize / restore below; any
        // other action is deferred out of the press handler like the caption buttons.
        var configuredDouble = isSecondClick ? ResolveGestureSlot("titleBar.doubleClick", e.KeyModifiers) : GestureCatalog.Default;
        if (isSecondClick && !string.Equals(configuredDouble, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
        {
            EndManualWindowDrag();
            ClearPendingChromeDrag();
            Dispatcher.UIThread.Post(async () => await ExecuteGestureActionAsync(configuredDouble), DispatcherPriority.Background);
            e.Handled = true;
            return;
        }

        if (isSecondClick && WindowState != WindowState.FullScreen)
        {
            EndManualWindowDrag();
            ClearPendingChromeDrag();
            ToggleMaximizeRestoreFromGesture();
            e.Handled = true;
            return;
        }

        // Do not enter the native move loop on press: it swallows the release, so a double-click on
        // the title bar would never be seen as one. Arm the drag and start it once the pointer moves
        // past the drag threshold, exactly like a native caption.
        if (WindowState == WindowState.FullScreen) return;
        _pendingChromeDragPress = e;
        _pendingChromeDragStart = pressPoint;
        e.Pointer.Capture(ChromeBorder);
        e.Handled = true;
    }

    /// <summary>
    /// Title-bar maximize / restore: the same native-truth toggle as the caption button (Avalonia's
    /// cached WindowState can be stale after Snap / Hide-Show). A synchronous SC_MAXIMIZE from inside
    /// a press handler (button still down, pointer captured) is ignored by Windows, so it is deferred
    /// like the caption buttons and verified.
    /// </summary>
    private void ToggleMaximizeRestoreFromGesture()
    {
        if (WindowState == WindowState.FullScreen) return;
        var restore = TryGetNativeZoomed(out var zoomed) ? zoomed : WindowState == WindowState.Maximized;
        Dispatcher.UIThread.Post(async () =>
        {
            TrySendNativeSystemCommand(restore ? ScRestore : ScMaximize);
            await Task.Delay(180);
            var nowZoomed = TryGetNativeZoomed(out var z) && z;
            if (restore ? nowZoomed : !nowZoomed)
                WindowState = restore ? WindowState.Normal : WindowState.Maximized;
        }, DispatcherPriority.Background);
    }

    private PointerPressedEventArgs? _pendingChromeDragPress;
    private PixelPoint _pendingChromeDragStart;

    private void ClearPendingChromeDrag()
    {
        var press = _pendingChromeDragPress;
        _pendingChromeDragPress = null;
        if (press is not null && ReferenceEquals(press.Pointer.Captured, ChromeBorder))
            press.Pointer.Capture(null);
    }

    private void ChromePointerMovedForDrag(object? sender, PointerEventArgs e)
    {
        if (_pendingChromeDragPress is not { } press) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ClearPendingChromeDrag(); return; }
        var now = this.PointToScreen(e.GetPosition(this));
        if (Math.Abs(now.X - _pendingChromeDragStart.X) < 4 && Math.Abs(now.Y - _pendingChromeDragStart.Y) < 4) return;
        _lastChromePressTick = 0; // a drag is not the first half of a double-click
        ClearPendingChromeDrag();
        BeginNativeWindowDrag(press);
    }


    private void ShowFullscreenChromeContextMenu() => BuildFullscreenChromeContextMenu().Open(ChromeBorder);

    /// <summary>The fullscreen title/tab bar right-click menu (also used by the menu coverage tests).</summary>
    internal ContextMenu BuildFullscreenChromeContextMenu()
    {
        var menu = new ContextMenu();
        var pin = MenuActions.Tag(new MenuItem
        {
            Header = _settings.FullscreenKeepTabBarOpen
                ? "Keep title/tab bar open in fullscreen  ✓"
                : "Keep title/tab bar open in fullscreen",
            Icon = MenuIcon("Fullscreen")
        }, "setting.fullscreen.keepTabBarOpen");
        pin.Click += (_, _) =>
        {
            _settings.FullscreenKeepTabBarOpen = !_settings.FullscreenKeepTabBarOpen;
            SaveSettingsAndPublish();
            ApplyChromeLayoutForWindowState();
            ApplyStatusBarSize();
        };
        var items = new List<MenuItem> { pin };
        // A directly visible, tickable Always-on-top toggle so fullscreen users do not have to leave
        // fullscreen (or hunt through a submenu) to pin Glide above other windows.
        if (!_wholeAppOverlayMode) items.Add(BuildAlwaysOnTopMenuItem());
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }

    private bool IsLeftWindowMoveAllowedForCurrentState()
    {
        if (WindowState == WindowState.FullScreen) return false;
        return _settings.LeftWindowDragBehavior switch
        {
            "Never move window" => false,
            "Allow move when maximized" => WindowState is WindowState.Normal or WindowState.Maximized,
            _ => WindowState == WindowState.Normal
        };
    }

    private void HomeBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_settings.BackgroundDragWindow || !IsLeftWindowMoveAllowedForCurrentState() || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        BeginNativeWindowDrag(e);
    }

    private void BeginNativeWindowDrag(PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen) return;
        var point = e.GetCurrentPoint(this);
        var physicalButtons = PhysicalMouseButtons(point);
        try
        {
            _diagnostics.Write("input", "native_window_drag_begin", new { source = e.Source?.GetType().Name, position = Position.ToString(), right = physicalButtons.Right });
            if (physicalButtons.Right)
            {
                _manualWindowDragPointer = e.Pointer;
                _manualWindowDragCursorStart = this.PointToScreen(e.GetPosition(this));
                _manualWindowDragWindowStart = Position;
                _manualWindowDragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (!physicalButtons.Left) return;
            PrepareForNativeWindowMove(e.Source);
            if (OperatingSystem.IsWindows() && TryRunNativeWindowMoveLoop())
            {
                e.Handled = true;
                return;
            }
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            EndManualWindowDrag();
        }
    }

    private void PrepareForNativeWindowMove(object? source)
    {
        var nativeCapture = OperatingSystem.IsWindows() ? GetNativeCapture() : IntPtr.Zero;
        _diagnostics.Write("input", "native_window_move_prepare", new
        {
            source = source?.GetType().Name,
            nativeCapture = nativeCapture.ToInt64(),
            manualWindowDrag = _manualWindowDragPointer is not null,
            zoom = Viewport.ZoomPercent,
            fullscreen = WindowState == WindowState.FullScreen
        });
        // Cancel only transient pointer ownership. CancelInteraction intentionally preserves the
        // committed bitmap/view transform/pan and therefore does not destroy a completed crop/zoom.
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { /* pointer capture already released; nothing to undo */ }
        }
    }

    private void BeginNativeResizeInteraction()
    {
        // WM_ENTERSIZEMOVE is the authoritative start of Windows' modal move/resize loop. Mark it
        // explicitly so no routed client gesture (overlay body drag, image pan/selection, deferred
        // right drag) can race the native border resize. This is especially important for windows
        // created via Ctrl+N, where restored view state can otherwise make a stale drag appear as
        // the image sliding horizontally while the outer window is being resized.
        _nativeSizeMoveActive = true;
        EnterNativeMoveLoopGuard();
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        _chromeMiddlePressPointer = null;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        if (_wholeAppOverlayControlsHost is { } overlayControls) overlayControls.IsVisible = false;
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { /* pointer capture already released; nothing to undo */ }
        }
        _diagnostics.Write("input", "native_resize_begin", new { Position, Width, Height });
    }

    private void ResetPointerStateAfterNativeResize()
    {
        // Called from WM_EXITSIZEMOVE, not inferred from a quiet SizeChanged interval. This is the
        // authoritative boundary where Windows has finished its modal non-client sizing loop.
        _nativeSizeMoveActive = false;
        LeaveNativeMoveLoopGuard();
        try { TrackNormalWindowPlacement(); SaveCurrentWholeAppOverlayPlacement(); } catch { /* placement bookkeeping during close is best effort */ }
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        _chromeMiddlePressPointer = null;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        _windowsBrowserFrame?.SetSelectionZoomOutPressed(false);
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { /* pointer capture already released; nothing to undo */ }
        }
        Dispatcher.UIThread.Post(() =>
        {
            EndManualWindowDrag();
            Viewport.CancelInteraction();
            Viewport.InvalidateVisual();
        }, DispatcherPriority.Input);
        _diagnostics.Write("input", "native_resize_pointer_state_reset", new { physicalLeft = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkLButton) & 0x8000) != 0, physicalRight = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkRButton) & 0x8000) != 0 });
    }

    private void BeginDeferredRightWindowDrag(IPointer pointer, PixelPoint screenStart, PixelPoint screenCurrent)
    {
        if (WindowState == WindowState.FullScreen) return;
        EndManualWindowDrag();
        _manualWindowDragPointer = pointer;
        _manualWindowDragCursorStart = screenStart;
        _manualWindowDragWindowStart = Position;
        var dx = screenCurrent.X - screenStart.X;
        var dy = screenCurrent.Y - screenStart.Y;
        _manualWindowDragMoved = true;
        try { pointer.Capture(this); } catch { /* pointer capture already released; nothing to undo */ }
        Position = new PixelPoint(_manualWindowDragWindowStart.X + dx, _manualWindowDragWindowStart.Y + dy);
        _diagnostics.Write("input", "deferred_right_window_drag_begin", new { dx, dy });
    }

    private void MainWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        var windowPointer = e.GetPosition(this);
        _lastWindowPointer = windowPointer;
        HandleFullscreenCursorMove(e, windowPointer);
        UpdateFullscreenChromeHover(windowPointer);
        UpdateSideTabsAutoHide(windowPointer);
        UpdateWholeAppOverlayChromeHover(windowPointer);
        var pointerProperties = e.GetCurrentPoint(this).Properties;
        _statusPointerButtonsDown = pointerProperties.IsLeftButtonPressed || pointerProperties.IsRightButtonPressed || pointerProperties.IsMiddleButtonPressed;
        UpdateStatusHoverReveal(windowPointer);
        if (_nativeSizeMoveActive) return;
        if (_manualWindowDragPointer is null || e.Pointer != _manualWindowDragPointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            EndManualWindowDrag();
            return;
        }
        var cursor = this.PointToScreen(e.GetPosition(this));
        var dx = cursor.X - _manualWindowDragCursorStart.X;
        var dy = cursor.Y - _manualWindowDragCursorStart.Y;
        if (!_manualWindowDragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
        {
            e.Handled = true;
            return;
        }
        _manualWindowDragMoved = true;
        Position = new PixelPoint(_manualWindowDragWindowStart.X + dx, _manualWindowDragWindowStart.Y + dy);
        e.Handled = true;
    }

    private void MainWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Safety reset for native right-zoom cursor feedback if release bubbles at window scope.
        _windowsBrowserFrame?.SetSelectionZoomOutPressed(false);
        var releasedProperties = e.GetCurrentPoint(this).Properties;
        _statusPointerButtonsDown = releasedProperties.IsLeftButtonPressed || releasedProperties.IsRightButtonPressed || releasedProperties.IsMiddleButtonPressed;
        if (_manualWindowDragPointer is null || e.Pointer != _manualWindowDragPointer) return;
        var wasStationaryRightClick = !_manualWindowDragMoved;
        EndManualWindowDrag();
        if (wasStationaryRightClick && ImageView.IsVisible) ShowViewerContextMenu();
        e.Handled = true;
    }

    private void UpdateFullscreenChromeHover(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        var windowedTopAutoHide = WindowState != WindowState.FullScreen && TopTabsAutoHideActive;
        if (!windowedTopAutoHide && (WindowState != WindowState.FullScreen || !_settings.AutoHideFullscreenChrome || _settings.FullscreenKeepTabBarOpen)) return;
        if (windowedTopAutoHide && _tabDrag.IsActive) return;

        // Hidden fullscreen chrome has no hit-test surface, so use a narrow hot edge to reveal it.
        // Once revealed, keep it visible while the pointer is inside its actual height, then hide it
        // immediately after leaving. The status surface is independent and remains available.
        var revealEdge = Math.Max(14, 18 / Math.Max(1.0, RenderScaling));
        var chromeHeight = Math.Max(44, ChromeBorder.Bounds.Height);
        if (!ChromeBorder.IsVisible)
        {
            if (pointer.Y <= revealEdge) ChromeBorder.IsVisible = true;
            return;
        }

        if (pointer.Y > chromeHeight + 8) ChromeBorder.IsVisible = false;
    }

    private Point _lastWindowPointer;
    private bool _statusMenuOpen;

    private void MainWindowPointerExited(object? sender, PointerEventArgs e)
    {
        // Leaving the window counts as leaving the bar at once (no delay); an open menu still keeps the bar (see below).
        _statusPointerButtonsDown = false;
        EvaluateStatusReveal(new Point(-10000, -10000), hideImmediately: true);
    }

    /// <summary>Auto-hide entry point (menu closed, pointer left, ...); the shared logic is <see cref="EvaluateStatusReveal"/>.</summary>
    private void UpdateStatusAutoHide(Point pointer)
    {
        if (_settings.StatusAutoHide) EvaluateStatusReveal(pointer);
    }

    private void UpdateStatusHoverReveal(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        EvaluateStatusReveal(pointer);
    }

    private readonly StatusRevealTracker _statusRevealTracker = new();
    private DispatcherTimer? _statusHideTimer;
    private bool _statusPointerButtonsDown;
    /// <summary>Clock for the hide delay (a field so tests can drive time).</summary>
    private Func<long> _statusRevealClock = () => Environment.TickCount64;

    /// <summary>
    /// Hover reveal for both the Auto-hide mode (bar fully hidden) and the collapsed bar with "reveal on hover" (bar shrunk to its
    /// expand button). One zone, one set of rules (<see cref="StatusBarAutoHide.RevealZone"/>, <see cref="StatusRevealTracker"/>):
    /// <list type="bullet">
    /// <item>SHOW: the pointer enters the invisible reveal area, which is exactly the box the status bar occupies when shown
    /// (<see cref="CurrentStatusBarBox"/>: read from the live layout, or measured when the bar is hidden / collapsed to the corner
    /// button, so it follows wrapping, resizing, alignment, size and fullscreen), adjusted by the user setting (default 0 =
    /// exactly that box, -40..+300 DIP). With the collapsed bar the parked expand button itself, plus a margin, also reveals.
    /// Only pointer-move tracking is used, so the area never blocks clicks or gestures on the image, and nothing
    /// is revealed while a mouse button is down (dragging).</item>
    /// <item>HIDE: once shown, the bar stays while the pointer is over the bar plus a small margin (NOT a grown reveal area, so
    /// it does not linger), over the spot the collapsed button occupied, or while one of its menus is open; after the pointer
    /// has been outside that region for a short delay it hides again. Leaving the window hides it at once. A hide never removes
    /// a collapsed bar outright: it returns to the single expand button.</item>
    /// </list>
    /// </summary>
    private void EvaluateStatusReveal(Point pointer, bool hideImmediately = false)
    {
        var autoHide = _settings.StatusAutoHide;
        if (!_settings.ShowStatusSurface || _statusSessionClosed || (!ImageView.IsVisible && !HomeHost.IsVisible) ||
            (!autoHide && (!_statusCollapsed || !_settings.StatusShowOnHoverWhenClosed)))
        {
            // Hover reveal is not in play. A collapsed bar left revealed (the option was switched off) returns to its button.
            if (!autoHide && _statusHoverRevealed) { _statusHoverRevealed = false; _statusHoverCollapsedRect = null; ApplyCollapsedStatusChrome(); }
            StopStatusHideTimer();
            return;
        }

        var barBox = CurrentStatusBarBox();
        var zone = StatusBarAutoHide.RevealZone(barBox, Bounds.Width, Bounds.Height, _settings.StatusRevealZoneExpand);
        var inZone = zone.Contains(pointer.X, pointer.Y);
        var overKeep = false;
        Rect? buttonRect = null;
        if (!_statusHoverRevealed)
        {
            // With the collapsed bar the parked expand button also reveals it (with the corner option off the invisible
            // full-size bar stays laid out, so the button's own bounds are used rather than the whole surface).
            if (!autoHide)
            {
                buttonRect = ActiveCollapseButtonRect();
                if (buttonRect is { } collapsedRect && collapsedRect.Inflate(StatusRevealCollapsedMargin).Contains(pointer)) inZone = true;
            }
        }
        else
        {
            var shownRect = ActiveStatusSurfaceRect() ?? new Rect(barBox.Left, barBox.Top, barBox.Width, barBox.Height);
            overKeep = shownRect.Inflate(StatusRevealKeepMargin).Contains(pointer) ||
                (!autoHide && _statusHoverCollapsedRect is { } previousRect && previousRect.Inflate(StatusRevealCollapsedMargin).Contains(pointer));
        }

        var menuOpen = _statusMenuOpen || _activeOwnedMenu is not null;
        var next = _statusRevealTracker.Update(true, _statusHoverRevealed, inZone, overKeep, menuOpen, _statusPointerButtonsDown,
            _statusRevealClock(), hideImmediately);
        if (_statusRevealTracker.HidePending) StartStatusHideTimer(); else StopStatusHideTimer();
        if (next == _statusHoverRevealed) return;
        _statusHoverRevealed = next;
        _statusHoverCollapsedRect = next ? buttonRect : null;
        if (autoHide) ApplyStatusVisibility();
        else ApplyCollapsedStatusChrome();
    }

    private void StartStatusHideTimer()
    {
        _statusHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        if (_statusHideTimer.IsEnabled) return;
        _statusHideTimer.Tick -= StatusHideTimerTick;
        _statusHideTimer.Tick += StatusHideTimerTick;
        _statusHideTimer.Start();
    }

    private void StopStatusHideTimer() => _statusHideTimer?.Stop();

    /// <summary>The pointer may be still: re-check with its last known position so the hide delay can elapse.</summary>
    private void StatusHideTimerTick(object? sender, EventArgs e) => EvaluateStatusReveal(_lastWindowPointer);

    private const double StatusRevealCollapsedMargin = 24;
    private const double StatusRevealKeepMargin = 16;
    private Rect? _statusHoverCollapsedRect;

    /// <summary>
    /// "Reserve status bar space when fitting": the viewport fits/centres the image in the area above
    /// the visible, expanded status bar (its height plus bottom margin). A collapsed bar (only the
    /// expand button, even while hover-revealed), a hidden bar and the default Off setting reserve
    /// nothing, so the image keeps using the full window under the floating bar.
    /// </summary>
    private void UpdateViewportStatusInset()
    {
        double inset = 0;
        if (_settings.StatusReserveFitSpace && !StatusCollapsedNow && !_settings.StatusAutoHide && !_wholeAppOverlayMode &&
            ViewerStatusSurface.IsVisible && ViewerStatusSurface.Bounds.Height > 1)
            inset = Math.Max(0, Viewport.Bounds.Bottom - ViewerStatusSurface.Bounds.Top);
        Viewport.FitBottomInset = inset;
    }

    /// <summary>Window-space bounds of the collapse/expand button currently shown (viewer or Home).</summary>
    private Rect? ActiveCollapseButtonRect()
    {
        Button? button = ImageView.IsVisible ? StatusCollapseButton
            : HomeHost.IsVisible ? _homeSurface?.StatusCollapseButton : null;
        if (button is null || !button.IsVisible || button.Bounds.Width <= 1) return null;
        var origin = button.TranslatePoint(default, this);
        return origin is { } p ? new Rect(p, button.Bounds.Size) : null;
    }

    /// <summary>Whether the bar is currently shrunk to just its corner button (collapsed with the corner option on, not hover-revealed).</summary>
    private bool StatusBarCollapsedToCornerNow =>
        _settings.ShowStatusSurface && StatusCollapsedNow && !_statusHoverRevealed && _settings.StatusCollapsedToCorner &&
        !_statusSessionClosed && WindowState != WindowState.FullScreen && !_wholeAppOverlayMode;

    private bool _statusProbing;
    /// <summary>Set only while the probe presents the bar in its revealed form (see <see cref="ProbeStatusBarBox"/>).</summary>
    private bool _statusPreviewReveal;
    private bool _statusProbeDirty = true;
    private bool _statusProbeHooked;
    private bool _statusProbeHome;
    private StatusRevealZone? _statusProbeBox;

    /// <summary>
    /// The window-space box the status bar occupies when it is shown, i.e. what the reveal area is derived from. While the bar is
    /// laid out (expanded, revealed, or collapsed with the corner option off, where it stays laid out but ghosted) this is its
    /// live rectangle, so wrapping into several rows, a thin strip in a wide window, Left / Center / Right and every resize are
    /// followed exactly. While it has no layout of its own (Auto-hide has hidden it, or it is collapsed to the corner button) the
    /// same bar is measured as if revealed (<see cref="ProbeStatusBarBox"/>); that result is re-measured after any window,
    /// setting or text change and is never reused across them.
    /// </summary>
    private StatusRevealZone CurrentStatusBarBox()
    {
        var home = !ImageView.IsVisible && HomeHost.IsVisible;
        Border? surface = home ? _homeSurface?.StatusSurface : ViewerStatusSurface;
        if (surface is not null && surface.IsVisible && surface.IsMeasureValid && surface.IsArrangeValid && surface.Bounds.Width > 1 &&
            !StatusBarCollapsedToCornerNow && surface.TranslatePoint(default, this) is { } origin)
            return new StatusRevealZone(origin.X, origin.Y, origin.X + surface.Bounds.Width, origin.Y + surface.Bounds.Height);
        return ProbeStatusBarBox(surface, home) ?? StatusBarAutoHide.FallbackBarRect(Bounds.Width, Bounds.Height);
    }

    private StatusRevealZone? ProbeStatusBarBox(Border? surface, bool home)
    {
        if (surface?.Parent is not Layoutable parent) return null;
        if (!_statusProbeHooked)
        {
            _statusProbeHooked = true;
            SizeChanged += (_, _) => _statusProbeDirty = true;
            StatusStatsText.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) _statusProbeDirty = true; };
        }
        if (!_statusProbeDirty && _statusProbeBox is { } cached && _statusProbeHome == home) return cached;
        var parentSize = parent.Bounds.Size;
        if (parentSize.Width < 2 || parentSize.Height < 2 || parent.TranslatePoint(default, this) is not { } origin) return null;

        // Present the bar exactly as revealing it would (same code path), measure it, and put everything back. Nothing is arranged
        // or drawn, and the restore invalidates layout so the next real pass lays the bar out as before.
        var wasVisible = surface.IsVisible;
        double width, height;
        HorizontalAlignment alignment;
        Thickness margin;
        _statusProbing = true;
        try
        {
            surface.IsVisible = true;
            _statusPreviewReveal = true;
            ApplyCollapsedStatusChrome();
            _statusPreviewReveal = false;
            // While the bar was hidden (Auto-hide) its descendants were never measured, so anything that changed meanwhile (the collapse
            // button hidden by Auto-hide, the stats text, icon sizes) left stale cached desired sizes below it, and a plain Measure
            // would reuse them (the box came out one collapse-button slot, ~40 DIP, wider than the revealed bar). Measure it from
            // scratch, exactly as revealing it does.
            surface.InvalidateMeasure();
            foreach (var descendant in surface.GetVisualDescendants()) (descendant as Layoutable)?.InvalidateMeasure();
            surface.Measure(parentSize);
            margin = surface.Margin;
            alignment = surface.HorizontalAlignment;
            width = Math.Max(0, surface.DesiredSize.Width - margin.Left - margin.Right);
            height = Math.Max(0, surface.DesiredSize.Height - margin.Top - margin.Bottom);
        }
        finally
        {
            _statusPreviewReveal = false;
            surface.IsVisible = wasVisible;
            ApplyCollapsedStatusChrome();
            surface.InvalidateMeasure();
            _statusProbing = false;
        }
        if (width <= 1 || height <= 1) return null;
        var left = alignment switch
        {
            HorizontalAlignment.Left => margin.Left,
            HorizontalAlignment.Right => parentSize.Width - margin.Right - width,
            _ => margin.Left + (parentSize.Width - margin.Left - margin.Right - width) / 2,
        };
        var bottom = parentSize.Height - margin.Bottom;
        var box = new StatusRevealZone(origin.X + left, origin.Y + bottom - height, origin.X + left + width, origin.Y + bottom);
        _statusProbeBox = box;
        _statusProbeHome = home;
        _statusProbeDirty = false;
        return box;
    }

    /// <summary>Window-space bounds of the status surface currently shown (viewer or Home).</summary>
    private Rect? ActiveStatusSurfaceRect()
    {
        Border? surface = ImageView.IsVisible ? ViewerStatusSurface
            : HomeHost.IsVisible ? _homeSurface?.StatusSurface : null;
        if (surface is null || !surface.IsVisible || surface.Bounds.Width <= 1) return null;
        var origin = surface.TranslatePoint(default, this);
        return origin is { } p ? new Rect(p, surface.Bounds.Size) : null;
    }

    private void EndManualWindowDrag()
    {
        if (_manualWindowDragPointer is { } pointer)
        {
            try { pointer.Capture(null); } catch { /* pointer capture already released; nothing to undo */ }
        }
        _manualWindowDragPointer = null;
        _manualWindowDragMoved = false;
    }

    private static bool IsInteractiveSource(object? source)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current.Classes.Contains("tabHit") || current.Classes.Contains("glideTab")) return true;
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current is Button or TextBox or ComboBox or CheckBox or NumericUpDown or ScrollBar or ListBox or ListBoxItem or Slider) return true;
        return false;
    }
}
