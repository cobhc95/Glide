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

/// <summary>Whole-window overlay mode and always-on-top chrome. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private void ApplyStartupWholeAppOverlayPreference()
    {
        // This exact flag is part of the compact first-frame policy, so startup Overlay can be
        // decided before full graph hydration without guessing from temporary defaults.
        if (_startupWholeAppOverlayPreferenceApplied) return;
        _startupWholeAppOverlayPreferenceApplied = true;
        if (_settings.AlwaysStartWholeAppOverlayMode)
            EnterWholeAppOverlayMode("startup_preference");
    }

    private Grid EnsureWholeAppOverlayControls()
    {
        if (_wholeAppOverlayControlsHost is { } existing) return existing;

        // Use the exact same visual chrome as per-image Window-in-Window overlays.  Host ownership is
        // intentionally different (top-level window vs canvas item), but close/opacity/resize geometry
        // and visual language must not drift.  The transparent hit targets below adapt those shared
        // regions to top-level window actions without putting this UI on the normal cold-image path.
        var chrome = new LegacyOverlayChrome
        {
            ChromeVisible = true,
            OpacityValue = _sessionOpacity,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ShowResizeGrip = false
        };

        var closeHit = new Button
        {
            Width = 27, Height = 27,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 7, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(closeHit, "Close Glide");
        closeHit.Click += (_, _) =>
        {
            // Whole-app Overlay X is a real close affordance, not a mode toggle.
            // If this process owns multiple Glide windows, closing Overlay exits the entire
            // current Glide application/session exactly as requested.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        };

        var opacityHit = new Slider
        {
            Minimum = OverlayChromePolicy.WholeWindowMinimumOpacity,
            Maximum = 1.0,
            Value = _sessionOpacity,
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Width = 22,
            Height = 130,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(opacityHit, "Overlay opacity");
        opacityHit.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty || !_wholeAppOverlayMode) return;
            var value = OverlayChromePolicy.ClampWholeWindowOpacity(opacityHit.Value);
            if (Math.Abs(TransparencySlider.Value - value * 100.0) > 0.01)
                TransparencySlider.Value = value * 100.0;
        };

        Button ZoomHit(string tip, bool zoomIn)
        {
            var button = new Button
            {
                Width = 30, Height = 30,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = zoomIn ? new Thickness(34, 0, 0, 8) : new Thickness(0, 0, 34, 8),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Opacity = 0.01, Focusable = false, ZIndex = 2
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Viewport.ZoomBy(zoomIn ? 1.15 : 1.0 / 1.15);
            return button;
        }
        var zoomOutHit = ZoomHit("Zoom out", false);
        var zoomInHit = ZoomHit("Zoom in", true);
        var topmostHit = new Button
        {
            Width = 30, Height = 30,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(118, 0, 0, 8),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(topmostHit, "Always on top");
        topmostHit.Click += (_, _) =>
        {
            _settings.OverlayAlwaysOnTop = !_settings.OverlayAlwaysOnTop;
            ApplyTopmostState(forceNative: true);
            SaveSettingsAndPublish();
        };

        // No synthetic bottom-right resize grip in whole-app Overlay mode.  The native
        // frame hit-test already resizes from the borders/corners and is substantially more stable
        // than mutating Width/Height from Thumb.DragDelta while the compositor is resizing.

        // Keep the top frame as an explicit drag affordance. In default Overlay interaction mode
        // the entire non-interactive body also drags via the MainRoot tunnel handler; when the user
        // selects Windowed interactions, only this frame band moves the top-level window.
        var dragBand = new Border
        {
            Height = OverlayChromePolicy.FrameDragBandDip,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 38, 0),
            Background = Brushes.Transparent,
            ZIndex = 1
        };
        ToolTip.SetTip(dragBand, "Drag to move the Overlay window");
        dragBand.PointerPressed += WholeAppOverlayChromePointerPressed;

        var host = new Grid
        {
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ZIndex = 1600
        };
        host.Children.Add(chrome);
        host.Children.Add(dragBand);
        host.Children.Add(closeHit);
        host.Children.Add(opacityHit);
        host.Children.Add(zoomOutHit);
        host.Children.Add(zoomInHit);
        host.Children.Add(topmostHit);
        host.SizeChanged += (_, _) =>
        {
            var slider = OverlayChromePolicy.SliderRect(host.Bounds.Size);
            opacityHit.Height = slider.Height;
        };
        WorkspaceLayer.Children.Add(host);

        chrome.TopmostActive = _settings.OverlayAlwaysOnTop;
        _wholeAppOverlayChromeVisual = chrome;
        _wholeAppOverlayOpacityHitTarget = opacityHit;
        _wholeAppOverlayResizeGrip = null;
        _wholeAppOverlayControlsHost = host;
        return host;
    }

    private bool TryGetWholeAppOverlayPixelRect(out Rect rect)
    {
        rect = default;
        if (!_wholeAppOverlayMode || Viewport.Bitmap is null || Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0) return false;
        var presentation = Viewport.GetPresentationRectForViewportSize(Viewport.Bounds.Size);
        if (presentation is null || presentation.Value.Width <= 0 || presentation.Value.Height <= 0) return false;
        var origin = Viewport.TranslatePoint(new Point(presentation.Value.X, presentation.Value.Y), WorkspaceLayer);
        if (origin is null) return false;
        var candidate = new Rect(origin.Value, presentation.Value.Size);
        var viewportOrigin = Viewport.TranslatePoint(new Point(0, 0), WorkspaceLayer);
        if (viewportOrigin is null) return false;
        var viewportRect = new Rect(viewportOrigin.Value, Viewport.Bounds.Size);
        var left = Math.Max(candidate.Left, viewportRect.Left);
        var top = Math.Max(candidate.Top, viewportRect.Top);
        var right = Math.Min(candidate.Right, viewportRect.Right);
        var bottom = Math.Min(candidate.Bottom, viewportRect.Bottom);
        if (right <= left || bottom <= top) return false;
        rect = new Rect(left, top, right - left, bottom - top);
        return true;
    }

    private void AlignWholeAppOverlayChromeToPixels()
    {
        if (_wholeAppOverlayControlsHost is not { } controls || !TryGetWholeAppOverlayPixelRect(out var rect)) return;
        controls.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        controls.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        controls.Width = rect.Width;
        controls.Height = rect.Height;
        controls.Margin = new Thickness(rect.X, rect.Y, 0, 0);
    }

    private bool IsPointerOverWholeAppOverlayPixels(Point windowPoint)
    {
        if (!TryGetWholeAppOverlayPixelRect(out var rect)) return false;
        var workspacePoint = this.TranslatePoint(windowPoint, WorkspaceLayer);
        return workspacePoint is { } p && rect.Contains(p);
    }

    private void WholeAppOverlayPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_wholeAppOverlayMode || _settings.WholeAppOverlayUseWindowedInteractions || Viewport.Bitmap is null || IsInteractiveSource(e.Source)) return;
        var windowPoint = e.GetPosition(this);
        if (!IsPointerOverWholeAppOverlayPixels(windowPoint)) return;
        var viewportPoint = e.GetPosition(Viewport);
        var zoomIn = e.Delta.Y > 0;
        if (_settings.InvertWheelDirection) zoomIn = !zoomIn;
        Viewport.ZoomBy(zoomIn ? 1.12 : 1.0 / 1.12, viewportPoint);
        AlignWholeAppOverlayChromeToPixels();
        e.Handled = true;
    }

    private void WholeAppOverlayBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || _settings.WholeAppOverlayUseWindowedInteractions) return;
        if (!physicalButtons.Left || IsInteractiveSource(e.Source) || !IsPointerOverWholeAppOverlayPixels(e.GetPosition(this))) return;
        BeginNativeWindowDrag(e);
        e.Handled = true;
    }

    private void WholeAppOverlayChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        BeginNativeWindowDrag(e);
        e.Handled = true;
    }

    private void EnterWholeAppOverlayMode(string reason = "context_menu")
    {
        if (_wholeAppOverlayMode) return;

        // Fullscreen has its own captured placement. Temporarily leave it so Overlay mode gets a
        // genuine movable window, then remember that fullscreen must be restored on exit.
        var restoreFullscreen = WindowState == WindowState.FullScreen;
        if (restoreFullscreen) ExitFullscreen(forceMaximized: false);

        var restoreState = WindowState;
        var restorePosition = Position;
        var restoreWidth = Width;
        var restoreHeight = Height;
        if (restoreState == WindowState.Maximized && _normalPlacementKnown)
        {
            restorePosition = _lastNormalWindowPosition;
            restoreWidth = _lastNormalWindowSize.Width;
            restoreHeight = _lastNormalWindowSize.Height;
        }

        _wholeAppOverlaySnapshot = new WholeAppOverlaySnapshot(
            restoreState, restorePosition, restoreWidth, restoreHeight, MinWidth, MinHeight, restoreFullscreen, _sessionOpacity);
        _wholeAppOverlayPreviousTransparencyFallback = TransparencyBackgroundFallback;
        _applyingWholeAppOverlayPlacement = true; // suppress transient restore/maximize events during the mode handoff
        _wholeAppOverlayMode = true;
        // Overlay geometry must be allowed to follow the image even when normal Glide has a tall
        // minimum height for its tab/status UI. Overlay chrome is floating, so smaller dimensions are safe.
        MinWidth = 80;
        MinHeight = 80;

        // Window-in-window mode should be a movable/resizable floating surface even if Glide had
        // previously been maximized. The previous state is restored exactly when the mode exits.
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (_normalPlacementKnown)
            {
                Position = _lastNormalWindowPosition;
                Width = Math.Max(MinWidth, _lastNormalWindowSize.Width);
                Height = Math.Max(MinHeight, _lastNormalWindowSize.Height);
            }
        }

        ApplySettingsVisuals();

        // Overlay is a genuinely transparent top-level surface: only image pixels/chrome render.
        // This preserves PNG alpha all the way to the Windows desktop and leaves letterbox/unused
        // aspect-ratio space completely see-through instead of painting Glide's viewport colour.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        Background = Brushes.Transparent;
        MainRoot.Background = Brushes.Transparent;
        WorkspaceLayer.Background = Brushes.Transparent;
        ImageView.Background = Brushes.Transparent;
        Viewport.SetBackgroundFillSuppressed(true);
        ApplyTopmostState(forceNative: true);
        ImageInfoOverlay.IsVisible = false;
        CornerCounterHost.IsVisible = false;
        var overlayControls = EnsureWholeAppOverlayControls();
        overlayControls.IsVisible = true;
        overlayControls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        TransparencyPanel.IsVisible = false;
        _applyingWholeAppOverlayPlacement = false;
        ApplyWholeAppOverlayPlacementForCurrentImage(preferRemembered: true);

        ApplyChromeLayoutForWindowState();
        ApplyStatusVisibility();
        UpdateViewportScrollbars();
        RefreshDynamicTooltips();
        _diagnostics.Write("window", "whole_app_overlay_entered", new
        {
            reason, restoreState = restoreState.ToString(), restoreFullscreen,
            transparency = ActualTransparencyLevel.ToString(), Position, Width, Height
        });
    }

    private void ExitWholeAppOverlayMode()
    {
        if (!_wholeAppOverlayMode) return;
        var snapshot = _wholeAppOverlaySnapshot;
        SaveCurrentWholeAppOverlayPlacement();
        _wholeAppOverlayMode = false;
        _wholeAppOverlayPlacementPath = null;
        _wholeAppOverlaySnapshot = null;

        if (_wholeAppOverlayControlsHost is { } overlayControls)
        {
            overlayControls.IsVisible = false;
            overlayControls.ContextMenu = null;
        }
        TransparencyPanel.IsVisible = false;
        Viewport.SetBackgroundFillSuppressed(false);
        // Restore normal UI opacity on the inner visual root only. The native top-level stays
        // transparent-capable permanently; this is what preserves per-pixel image alpha when
        // Overlay is entered again without recreating the HWND.
        if (Application.Current?.Resources["BrushWindow"] is IBrush normalWindowBrush)
            MainRoot.Background = normalWindowBrush;
        else
            MainRoot.Background = Brushes.Black;
        WorkspaceLayer.Background = null;
        ImageView.Background = null;
        // Keep the top-level permanently transparency-capable. The normal theme paints an opaque
        // background outside Overlay mode, so this has no visual effect in ordinary windows, while
        // re-entering Overlay never depends on recreating/reconfiguring an already-shown HWND.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        if (_wholeAppOverlayPreviousTransparencyFallback is { } fallback)
            TransparencyBackgroundFallback = fallback;
        _wholeAppOverlayPreviousTransparencyFallback = null;

        if (snapshot is not null)
        {
            _sessionOpacity = OverlayChromePolicy.ClampWholeWindowOpacity(snapshot.SessionOpacity);
            MinWidth = snapshot.MinWidth;
            MinHeight = snapshot.MinHeight;
            Opacity = _sessionOpacity;
            TransparencySlider.Value = _sessionOpacity * 100.0;
            WindowState = WindowState.Normal;
            if (snapshot.Width > 0 && snapshot.Height > 0)
            {
                Width = Math.Max(MinWidth, snapshot.Width);
                Height = Math.Max(MinHeight, snapshot.Height);
                Position = snapshot.Position;
            }
            if (snapshot.RestoreState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }

        ApplySettingsVisuals();
        if (snapshot?.RestoreFullscreen == true && WindowState != WindowState.FullScreen)
            EnterFullscreen();

        _diagnostics.Write("window", "whole_app_overlay_exited", new
        {
            restoredState = WindowState.ToString(), restoredFullscreen = snapshot?.RestoreFullscreen == true,
            Position, Width, Height
        });
    }

    private void UpdateWholeAppOverlayChromeHover(Point pointer)
    {
        if (!_wholeAppOverlayMode || !IsActive || _nativeSizeMoveActive) return;
        var controls = _wholeAppOverlayControlsHost;
        if (controls is null) return;
        AlignWholeAppOverlayChromeToPixels();
        var overPixels = IsPointerOverWholeAppOverlayPixels(pointer);
        // Overlay chrome/glow belongs to the rendered pixels, not the transparent top-level window.
        // It appears only while the pointer is actually over visible image pixels.
        controls.IsVisible = overPixels;
    }

    private void ToggleAlwaysStartWholeAppOverlayMode()
    {
        _settings.AlwaysStartWholeAppOverlayMode = !_settings.AlwaysStartWholeAppOverlayMode;
        SaveSettingsAndPublish();
        if (_wholeAppOverlayMode && _wholeAppOverlayControlsHost is { } controls) controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_startup_preference", new { enabled = _settings.AlwaysStartWholeAppOverlayMode });
    }

    private void ToggleWholeAppOverlayInteractionMode()
    {
        _settings.WholeAppOverlayUseWindowedInteractions = !_settings.WholeAppOverlayUseWindowedInteractions;
        SaveSettingsAndPublish();
        ApplySettingsVisuals();
        if (_wholeAppOverlayControlsHost is { } controls)
            controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_interaction_mode", new
        {
            windowed = _settings.WholeAppOverlayUseWindowedInteractions,
            mode = _settings.WholeAppOverlayUseWindowedInteractions ? "windowed" : "overlay"
        });
    }

    private void AppendWholeAppOverlayMenuItems(List<MenuItem> items)
    {
        if (!_wholeAppOverlayMode)
        {
            var enter = new MenuItem { Header = "Enter Overlay (Window-in-Window) Mode", Icon = MenuIcon("Overlays") };
            enter.Click += (_, _) => EnterWholeAppOverlayMode();
            items.Add(enter);
            return;
        }

        var interactions = new MenuItem
        {
            Header = _settings.WholeAppOverlayUseWindowedInteractions
                ? "Interaction: Windowed mode  ✓"
                : "Interaction: Overlay mode  ✓",
            Icon = MenuIcon("Mouse")
        };
        interactions.Click += (_, _) => ToggleWholeAppOverlayInteractionMode();
        items.Add(interactions);
        var sizeMenu = new MenuItem { Header = "Size", Icon = MenuIcon("Maximize") };
        foreach (var percent in new[] { 25, 50, 75, 100, 125, 150, 200 })
        {
            var choice = new MenuItem { Header = $"{percent}%" };
            choice.Click += (_, _) => SetWholeAppOverlaySize(percent);
            sizeMenu.Items.Add(choice);
        }
        items.Add(sizeMenu);
        items.Add(new MenuItem { Header = "-" });

        var always = new MenuItem
        {
            Header = _settings.AlwaysStartWholeAppOverlayMode
                ? "Always enter Glide in Overlay mode  ✓"
                : "Always enter Glide in Overlay mode",
            Icon = MenuIcon("Overlays")
        };
        always.Click += (_, _) => ToggleAlwaysStartWholeAppOverlayMode();
        items.Add(always);

        var topmost = new MenuItem { Header = _settings.OverlayAlwaysOnTop ? "Always on top  ✓" : "Always on top", Icon = MenuIcon("AlwaysOnTop") };
        topmost.Click += (_, _) => AlwaysOnTopClicked(null, new RoutedEventArgs());
        items.Add(topmost);

        items.Add(new MenuItem { Header = "-" });
        var exit = new MenuItem { Header = "Exit Overlay mode", Icon = MenuIcon("Close") };
        exit.Click += (_, _) => ExitWholeAppOverlayMode();
        items.Add(exit);
    }

    private void SetWholeAppOverlaySize(int percent)
    {
        if (!_wholeAppOverlayMode || WindowState != WindowState.Normal) return;
        var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(Position)) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var pixels = Viewport.Bitmap?.PixelSize;
        var sourceWidth = Math.Max(1, _currentPixelWidth > 0 ? _currentPixelWidth : pixels?.Width ?? 1);
        var sourceHeight = Math.Max(1, _currentPixelHeight > 0 ? _currentPixelHeight : pixels?.Height ?? 1);
        var area = screen.WorkingArea;
        var factor = Math.Min(percent / 100.0,
            Math.Min(area.Width / (scale * sourceWidth), area.Height / (scale * sourceHeight)));
        var width = sourceWidth * factor;
        var height = sourceHeight * factor;
        var x = Math.Clamp(Position.X + (int)Math.Round((Bounds.Width - width) * scale / 2), area.X, area.Right - (int)Math.Ceiling(width * scale));
        var y = Math.Clamp(Position.Y + (int)Math.Round((Bounds.Height - height) * scale / 2), area.Y, area.Bottom - (int)Math.Ceiling(height * scale));
        Position = new PixelPoint(x, y);
        Width = width;
        Height = height;
        SaveCurrentWholeAppOverlayPlacement();
    }

    private ContextMenu BuildWholeAppOverlayOnlyContextMenu()
    {
        var items = new List<MenuItem>();
        AppendWholeAppOverlayMenuItems(items);
        return PrepareOwnedContextMenu(new ContextMenu { ItemsSource = items });
    }

    private void WholeAppOverlayContextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || ImageView.IsVisible) return;
        // Choosing an item in (or right-clicking) another Glide menu must never also raise this one.
        if (_activeOwnedMenu is { IsOpen: true } || Environment.TickCount64 - _ownedMenuClosedTick < 500) return;

        // Respect the more specific tab/browser/control context menus. Empty Home/Explorer/chrome
        // areas receive this tiny whole-app menu, making the mode discoverable throughout Glide.
        for (var current = e.Source as Control; current is not null && !ReferenceEquals(current, MainRoot); current = current.GetVisualParent() as Control)
            if (current.ContextMenu is not null || ReferenceEquals(current, ViewerStatusSurface) || ReferenceEquals(current, _homeSurface?.StatusSurface)) return;

        var target = e.Source as Control ?? MainRoot;
        BuildWholeAppOverlayOnlyContextMenu().Open(target);
        e.Handled = true;
    }
}
