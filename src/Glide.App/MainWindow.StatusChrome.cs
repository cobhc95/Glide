using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Diagnostics;
using Glide.App.Platform;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Imaging;
using Glide.Input;
using Glide.Diagnostics.Runtime;
using Microsoft.VisualBasic.FileIO;

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
        if (!_statusCollapsed)
        {
            _statusHoverRevealed = false;
            _collapseAnchorsCaptured = false;
            _collapseAnchorViewer = null;
            _collapseAnchorHome = null;
        }
        else if (!_collapseAnchorsCaptured)
        {
            // Still in the expanded layout: remember where the collapse button sits so the
            // collapsed bar (just that button) lands exactly on it.
            _collapseAnchorsCaptured = true;
            _collapseAnchorViewer = StatusButtonAnchorX(ViewerStatusSurface, StatusCollapseButton);
            if (_homeSurface is not null)
                _collapseAnchorHome = StatusButtonAnchorX(_homeSurface.StatusSurface, _homeSurface.StatusCollapseButton);
        }
        var fullscreen = WindowState == WindowState.FullScreen;
        var allowed = _settings.ShowStatusSurface && (!fullscreen || _settings.FullscreenStatusAlwaysOn);
        var expanded = allowed && !_statusCollapsed && !_statusSessionClosed;
        // Windowed collapse keeps the bar present but collapsed to its own button: that same
        // button turns into the expand arrow (UpdateCollapseButtonGlyphs) and clicking it again
        // expands the bar. No separate restore arrow is left in the corner, and collapse never
        // closes the bar (the X / CloseStatusClicked is the only close path). The corner button
        // remains only for fullscreen, where the bar is suppressed rather than collapsed.
        var collapsedOnly = _settings.ShowStatusSurface && _statusCollapsed && !_statusSessionClosed && !fullscreen;
        ViewerStatusSurface.IsVisible = (expanded || collapsedOnly) && ImageView.IsVisible;
        if (_homeSurface is not null) _homeSurface.StatusSurface.IsVisible = (expanded || collapsedOnly) && HomeHost.IsVisible && !fullscreen;
        StatusRestoreButton.IsVisible = _settings.ShowStatusSurface && fullscreen &&
            (_statusCollapsed || !_settings.FullscreenStatusAlwaysOn) &&
            (ImageView.IsVisible || HomeHost.IsVisible);
        UpdateCollapseButtonGlyphs();
        ApplyCollapsedStatusChrome();
    }

    /// <summary>
    /// Collapsed means "only the collapse/expand button remains": hide the stats, every control
    /// group and the X, leaving the single button (now showing the expand arrow) in place.
    /// Hover reveal temporarily shows the full bar again without clearing the collapsed state.
    /// </summary>
    private void ApplyCollapsedStatusChrome()
    {
        var collapsedOnly = _settings.ShowStatusSurface && _statusCollapsed && !_statusHoverRevealed &&
            !_statusSessionClosed && WindowState != WindowState.FullScreen && !_wholeAppOverlayMode;
        if (StatusControlsPanel is not null) StatusControlsPanel.IsVisible = !collapsedOnly;
        if (StatusStatsHost is not null) StatusStatsHost.IsVisible = !collapsedOnly && Bounds.Width >= 260;
        if (StatusCloseButton is not null) StatusCloseButton.IsVisible = !collapsedOnly && _settings.StatusShowClose;
        // Collapsed bar = only the button, no empty strip. It is pinned so the button stays
        // exactly where the collapse button was.
        if (collapsedOnly) PinCollapsedSurface(ViewerStatusSurface, StatusCollapseButton, _collapseAnchorViewer);
        else ApplyStatusSurfaceAlignment(ViewerStatusSurface);
        SetStatusSurfaceChromeless(ViewerStatusSurface, collapsedOnly);
        if (_homeSurface is not null)
        {
            _homeSurface.StatusText.IsVisible = !collapsedOnly;
            foreach (var button in _homeSurface.StatusControlsPanel.Children.OfType<Button>())
                button.IsVisible = !collapsedOnly || ReferenceEquals(button, _homeSurface.StatusCollapseButton);
            if (collapsedOnly) PinCollapsedSurface(_homeSurface.StatusSurface, _homeSurface.StatusCollapseButton, _collapseAnchorHome);
            else ApplyStatusSurfaceAlignment(_homeSurface.StatusSurface);
            SetStatusSurfaceChromeless(_homeSurface.StatusSurface, collapsedOnly);
        }
    }

    /// <summary>A collapsed bar shows only its expand button: no panel fill, border or shadow around it.</summary>
    private static void SetStatusSurfaceChromeless(Border surface, bool chromeless)
    {
        if (chromeless)
        {
            surface.Background = Brushes.Transparent;
            surface.BorderBrush = Brushes.Transparent;
            surface.BoxShadow = default;
        }
        else
        {
            surface.ClearValue(Border.BackgroundProperty);
            surface.ClearValue(Border.BorderBrushProperty);
            surface.ClearValue(Border.BoxShadowProperty);
        }
    }

    private static double? StatusButtonAnchorX(Border surface, Button button)
    {
        if (!surface.IsVisible || surface.Bounds.Width <= 1 || !button.IsVisible) return null;
        if (surface.GetVisualParent() is not Visual parent) return null;
        return button.TranslatePoint(default, parent)?.X;
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

    private void PinCollapsedSurface(Border surface, Button button, double? anchorX)
    {
        surface.Width = double.NaN;
        if (_settings.StatusCollapsedToCorner)
        {
            surface.HorizontalAlignment = HorizontalAlignment.Right;
            surface.Margin = new Thickness(14, 0, 14, 14);
            return;
        }
        if (anchorX is null) { ApplyStatusSurfaceAlignment(surface); return; }
        surface.HorizontalAlignment = HorizontalAlignment.Left;
        surface.Margin = new Thickness(0, 0, 0, 14);
        var anchor = anchorX.Value;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_statusCollapsed || !surface.IsVisible) return;
            surface.UpdateLayout();
            var dx = button.TranslatePoint(default, surface)?.X ?? 0;
            surface.Margin = new Thickness(Math.Max(0, anchor - dx), 0, 0, 14);
        }, DispatcherPriority.Loaded);
    }

    private void UpdateCollapseButtonGlyphs()
    {
        void Apply(Button? button)
        {
            if (button?.Content is not GlideIconView glyph) return;
            glyph.Kind = _statusCollapsed ? "Expand" : "Collapse";
            ToolTip.SetTip(button, _statusCollapsed
                ? $"[Expand status bar ({ShortcutFor(GlideCommand.ToggleStatusSurface)})]"
                : $"[Collapse status bar ({ShortcutFor(GlideCommand.ToggleStatusSurface)})]");
        }
        Apply(StatusCollapseButton);
        Apply(_homeSurface?.StatusCollapseButton);
    }

    private void ApplyChromeLayoutForWindowState()
    {
        if (MainRoot.RowDefinitions.Count < 2) return;

        if (_wholeAppOverlayMode)
        {
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = false;
            FullscreenCornerCloseHit.IsVisible = false;
            return;
        }

        var fullscreen = WindowState == WindowState.FullScreen;
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

        if (isSecondClick && WindowState != WindowState.FullScreen)
        {
            EndManualWindowDrag();
            // Same native-truth toggle as the caption button: Avalonia's cached WindowState can be
            // stale after Snap / Hide-Show, which made this double-click restore a half-size window.
            var restore = TryGetNativeZoomed(out var zoomed) ? zoomed : WindowState == WindowState.Maximized;
            ClearPendingChromeDrag();
            // A synchronous SC_MAXIMIZE from inside the press handler (button still down, pointer
            // captured) is ignored by Windows. Defer it like the caption buttons do, and verify.
            Dispatcher.UIThread.Post(async () =>
            {
                TrySendNativeSystemCommand(restore ? ScRestore : ScMaximize);
                await Task.Delay(180);
                var nowZoomed = TryGetNativeZoomed(out var z) && z;
                if (restore ? nowZoomed : !nowZoomed)
                    WindowState = restore ? WindowState.Normal : WindowState.Maximized;
            }, DispatcherPriority.Background);
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


    private void ShowFullscreenChromeContextMenu()
    {
        var menu = new ContextMenu();
        var pin = new MenuItem
        {
            Header = _settings.FullscreenKeepTabBarOpen
                ? "Keep title/tab bar open in fullscreen  ✓"
                : "Keep title/tab bar open in fullscreen",
            Icon = MenuIcon("Fullscreen")
        };
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
        PrepareOwnedContextMenu(menu).Open(ChromeBorder);
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
            try { ReleaseNativeCapture(); } catch { }
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
            try { ReleaseNativeCapture(); } catch { }
        }
        _diagnostics.Write("input", "native_resize_begin", new { Position, Width, Height });
    }

    private void ResetPointerStateAfterNativeResize()
    {
        // Called from WM_EXITSIZEMOVE, not inferred from a quiet SizeChanged interval. This is the
        // authoritative boundary where Windows has finished its modal non-client sizing loop.
        _nativeSizeMoveActive = false;
        LeaveNativeMoveLoopGuard();
        try { TrackNormalWindowPlacement(); SaveCurrentWholeAppOverlayPlacement(); } catch { }
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        _chromeMiddlePressPointer = null;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        _windowsBrowserFrame?.SetSelectionZoomOutPressed(false);
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { }
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
        try { pointer.Capture(this); } catch { }
        Position = new PixelPoint(_manualWindowDragWindowStart.X + dx, _manualWindowDragWindowStart.Y + dy);
        _diagnostics.Write("input", "deferred_right_window_drag_begin", new { dx, dy });
    }

    private void MainWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        var windowPointer = e.GetPosition(this);
        HandleFullscreenCursorMove(e, windowPointer);
        UpdateFullscreenChromeHover(windowPointer);
        UpdateWholeAppOverlayChromeHover(windowPointer);
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
        if (_manualWindowDragPointer is null || e.Pointer != _manualWindowDragPointer) return;
        var wasStationaryRightClick = !_manualWindowDragMoved;
        EndManualWindowDrag();
        if (wasStationaryRightClick && ImageView.IsVisible) ShowViewerContextMenu();
        e.Handled = true;
    }

    private void UpdateFullscreenChromeHover(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        if (WindowState != WindowState.FullScreen || !_settings.AutoHideFullscreenChrome || _settings.FullscreenKeepTabBarOpen) return;

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

    private void UpdateStatusHoverReveal(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        if (!_statusCollapsed || !_settings.StatusShowOnHoverWhenClosed || !_settings.ShowStatusSurface) return;
        var edge = Math.Max(12, 16 / Math.Max(1.0, RenderScaling));
        var nearBottom = pointer.Y >= Math.Max(0, Bounds.Height - edge);
        // Hover reveal expands the collapsed-only bar in place; leaving the bar returns it to
        // the single expand button. It never hides the surface outright.
        if (nearBottom && (ImageView.IsVisible || HomeHost.IsVisible))
        {
            if (!_statusHoverRevealed) { _statusHoverRevealed = true; ApplyCollapsedStatusChrome(); }
        }
        else if (_statusHoverRevealed && pointer.Y < Bounds.Height - Math.Max(edge, ViewerStatusSurface.Bounds.Height + 10))
        {
            _statusHoverRevealed = false;
            ApplyCollapsedStatusChrome();
        }
    }

    private void EndManualWindowDrag()
    {
        if (_manualWindowDragPointer is { } pointer)
        {
            try { pointer.Capture(null); } catch { }
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

    private static bool IsInsideHomeCard(object? source)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current is Border border && border.Classes.Contains("homeCard")) return true;
        return false;
    }
}
