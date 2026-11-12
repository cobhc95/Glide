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

/// <summary>Transport buttons, fitting, status surface, transparency and overlay placement. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private async void PreviousClicked(object? sender, RoutedEventArgs e) => await NavigateMouseAsync(-1);
    private async void NextClicked(object? sender, RoutedEventArgs e) => await NavigateMouseAsync(1);
    private async void FirstClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.FirstImage);
    private async void LastClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.LastImage);

    private Task NavigateEdgeAsync(int direction) => RunSerializedNavigationAsync(async () =>
    {
        if (_navigator.Count == 0) return;

        StopHeldNavigation(settle: false, reason: direction < 0 ? "first_image" : "last_image");
        var oldIndex = _navigator.Index;
        var oldPath = _navigator.Current;
        var navigationEpoch = _navigationCommandEpoch;
        var workspaceEpoch = Volatile.Read(ref _workspaceEpoch);
        var targetPath = direction < 0 ? _navigator.First() : _navigator.Last();
        var targetIndex = _navigator.Index;
        _diagnostics.Write("navigation", "edge_navigation_requested", new
        {
            direction, oldIndex, targetIndex, targetPath
        });
        Interlocked.Increment(ref _navigationCommandEpoch);
        _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);

        // Edge navigation is ordinary navigation. Let the normal bounded preview become visible
        // first; the existing refinement path can promote the full frame afterwards.
        var outcome = await PresentCurrentAsync();
        _diagnostics.Write("navigation", "edge_navigation_presented", new
        {
            outcome = outcome.Status.ToString(), resultingIndex = _navigator.Index,
            request = outcome.Request.ImageRequestId
        });
        if (outcome.Accepted) return;

        // This command owns the serialization gate, but still verify the state before restoring it
        // so a future asynchronous caller cannot overwrite a newer workspace/navigation command.
        var canRollback = navigationEpoch + 1 == _navigationCommandEpoch &&
            workspaceEpoch == Volatile.Read(ref _workspaceEpoch) &&
            _navigator.Index == targetIndex &&
            string.Equals(_navigator.Current, targetPath, StringComparison.OrdinalIgnoreCase);
        if (!canRollback || oldPath is null) return;

        _navigator.SelectIndex(oldIndex);
        _currentPath = oldPath;
        _loader.SetActivePath(oldPath);
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        UpdateStatusStats();
        _diagnostics.Write("navigation", "edge_navigation_rollback", new
        {
            oldIndex, failedTargetIndex = targetIndex, outcome = outcome.Status.ToString()
        });
    });
    private void FitClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.Fit(); }
    private void FitImageClicked(object? sender, RoutedEventArgs e) => ApplyStatusBarFit("Fit image", Viewport.Fit);
    private void FitWidthClicked(object? sender, RoutedEventArgs e) => ApplyStatusBarFit("Fit width", Viewport.FitWidth);
    private void FitWindowClicked(object? sender, RoutedEventArgs e) => ApplyStatusBarFit("Fit to window", Viewport.FitWindow);
    private void FitHeightClicked(object? sender, RoutedEventArgs e) => ApplyStatusBarFit("Fit height", Viewport.FitHeight);
    private void ActualSizeClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.ActualSize(); }

    /// <summary>
    /// Applies a status bar Fit command to the current image and then, according to the configured
    /// <see cref="GlideSettingsState.StatusFitScope"/>, carries the chosen mode forward so the rest
    /// of the pictures follow it. "Only the current image" leaves the persisted default untouched;
    /// "This session" updates a live session default; "This session and future sessions" also
    /// persists the mode as the new DefaultViewMode so future launches adopt it.
    /// </summary>
    private void ApplyStatusBarFit(string viewMode, Action applyToViewport)
    {
        _selectionZoomDemandPath = null;
        applyToViewport();

        var scope = _settings.StatusFitScope;
        if (string.Equals(scope, "Only the current image", StringComparison.OrdinalIgnoreCase))
            return;

        _sessionDefaultViewMode = viewMode;
        if (!string.Equals(scope, "This session and future sessions", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(_settings.DefaultViewMode, viewMode, StringComparison.OrdinalIgnoreCase)) return;
        _settings.DefaultViewMode = viewMode;
        SaveSettingsAndPublish();
    }

    private string EffectiveDefaultViewMode() =>
        string.IsNullOrWhiteSpace(_sessionDefaultViewMode) ? _settings.DefaultViewMode : _sessionDefaultViewMode;

    private void ZoomInClicked(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1.15);
    private void ZoomOutClicked(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1 / 1.15);
    private async void SlideshowClicked(object? sender, RoutedEventArgs e) => await ToggleSlideshowAsync();
    private void ReliableStatusSurfaceButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = (e.Source as Visual)?.FindAncestorOfType<Button>() ?? (e.Source as Button);
        if (button is not null)
            ReliableCommandButtonPressed(button, e);
    }

    private async void ReliableCommandButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        _diagnostics.Write("input", "reliable_button_press", new { button = button.Name ?? "<unnamed>", state = WindowState.ToString() });
        await ExecuteStatusButtonAsync(button);
    }

    /// <summary>Runs the action behind a status-bar button (also used by the "..." overflow menu).</summary>
    internal async Task ExecuteStatusButtonAsync(Button button)
    {
        if (ReferenceEquals(button, StatusSaveButton)) SaveImageClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusSaveAsButton)) SaveImageAsClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusPreviousButton)) await ExecuteCommandAsync(GlideCommand.PreviousImage);
        else if (ReferenceEquals(button, StatusNextButton)) await ExecuteCommandAsync(GlideCommand.NextImage);
        else if (ReferenceEquals(button, StatusFirstButton)) await ExecuteCommandAsync(GlideCommand.FirstImage);
        else if (ReferenceEquals(button, StatusLastButton)) await ExecuteCommandAsync(GlideCommand.LastImage);
        else if (ReferenceEquals(button, SlideshowButton)) await ToggleSlideshowAsync();
        else if (ReferenceEquals(button, SlideshowStopButton)) SlideshowStopClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusZoomOutButton)) Viewport.ZoomBy(1 / 1.15);
        else if (ReferenceEquals(button, StatusZoomInButton)) Viewport.ZoomBy(1.15);
        else if (ReferenceEquals(button, StatusFitImageButton)) ApplyStatusBarFit("Fit image", Viewport.Fit);
        else if (ReferenceEquals(button, StatusFitWidthButton)) ApplyStatusBarFit("Fit width", Viewport.FitWidth);
        else if (ReferenceEquals(button, StatusFitWindowButton)) ApplyStatusBarFit("Fit to window", Viewport.FitWindow);
        else if (ReferenceEquals(button, StatusFitHeightButton)) ApplyStatusBarFit("Fit height", Viewport.FitHeight);
        else if (ReferenceEquals(button, StatusInfoButton)) InfoClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusOptionsButton) || (_homeSurface is not null && ReferenceEquals(button, _homeSurface.OptionsButton))) SettingsClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusCollapseButton) || (_homeSurface is not null && ReferenceEquals(button, _homeSurface.StatusCollapseButton))) CollapseStatusClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusCloseButton)) CloseStatusClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, OverlayAddButton)) { _diagnostics.Write("overlay", "status_command", new { command = "add" }); OverlayAddClicked(button, new RoutedEventArgs()); }
        else if (ReferenceEquals(button, OverlayLoadButton)) { _diagnostics.Write("overlay", "status_command", new { command = "load" }); OverlayLoadClicked(button, new RoutedEventArgs()); }
        else if (ReferenceEquals(button, OverlaySaveButton)) { if (_overlays?.HasOverlays == true) { _diagnostics.Write("overlay", "status_command", new { command = "save" }); OverlaySaveClicked(button, new RoutedEventArgs()); } }
        else if (ReferenceEquals(button, OverlayClearButton)) { if (_overlays?.HasOverlays == true) { _diagnostics.Write("overlay", "status_command", new { command = "clear" }); OverlayClearClicked(button, new RoutedEventArgs()); } }
        else if (ReferenceEquals(button, CaptionMinimizeButton)) CaptionMinimizeClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, CaptionMaximizeButton)) CaptionMaximizeClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, CaptionCloseButton)) CaptionCloseClicked(button, new RoutedEventArgs());
    }

    private void InfoClicked(object? sender, RoutedEventArgs e)
    {
        if (_wholeAppOverlayMode) { ImageInfoOverlay.IsVisible = false; return; }
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        if (ImageInfoOverlay.IsVisible)
        {
            ImageInfoOverlay.IsVisible = false;
            _diagnostics.Write("image_info", "overlay_toggled", new { visible = false, path = _currentPath });
            return;
        }
        UpdateImageInfoOverlayText();
        ImageInfoOverlay.IsVisible = true;
        _diagnostics.Write("image_info", "overlay_toggled", new { visible = true, path = _currentPath });
    }

    private void UpdateImageInfoOverlayText()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var path = _currentPath;
        var created = "";
        var modified = "";
        try
        {
            var info = new FileInfo(path);
            created = $"\nCreated: {info.CreationTime:g}";
            modified = $"\nModified: {info.LastWriteTime:g}";
        }
        catch { }
        var metadata = (_currentMetadata.HasExif || _currentMetadata.FrameCount is not null) ? "\n" + _currentMetadata.ToDisplayText() : "";
        var format = CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant();
        ImageInfoText.Text = $"{Path.GetFileName(path)}\n{path}\nFormat: {format}   Dimensions: {_currentPixelWidth} × {_currentPixelHeight}\nFile size: {FormatFileSize(_currentFileSize)}   Index: {Math.Max(0, _navigator.Index + 1)} / {_navigator.Count}\nZoom: {Viewport.ZoomPercent}%   Last decode: {_currentDecodeMs:F1} ms{created}{modified}{metadata}";
    }

    private async Task LoadMetadataAfterPaintAsync(string path, ImageRequestContext request, CancellationToken token)
    {
        ImageMetadata metadata;
        try
        {
            metadata = await ImageMetadataService.ReadAsync(_decoderBackend, path, token);
        }
        catch (OperationCanceledException) { return; }
        if (!IsRequestCurrent(request, requirePresented: true) ||
            !string.Equals(path, request.Path, StringComparison.OrdinalIgnoreCase)) return;
        _currentMetadata = metadata;
        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
        _diagnostics.Write("metadata", "loaded", new { path, request = request.ImageRequestId, exif = metadata.HasExif, orientation = metadata.Orientation });
    }

    private void CollapseStatusClicked(object? sender, RoutedEventArgs e)
    {
        // The same button collapses an expanded bar and, on the hover-revealed collapsed bar,
        // expands it again.
        if (_statusCollapsed)
        {
            RestoreStatusClicked(sender, e);
            return;
        }
        _statusCollapsed = true;
        ApplyStatusVisibility();
        _diagnostics.Write("status", "collapsed", new { surface = HomeHost.IsVisible ? "home" : "viewer" });
    }

    private void RestoreStatusClicked(object? sender, RoutedEventArgs e)
    {
        _settings.ShowStatusSurface = true;
        _statusCollapsed = false;
        _statusSessionClosed = false;
        SaveSettingsAndPublish();
        ApplyStatusVisibility();
        _diagnostics.Write("status", "expanded", new { surface = HomeHost.IsVisible ? "home" : "viewer" });
    }

    private void CloseStatusClicked(object? sender, RoutedEventArgs e)
    {
        // X is a real close, not a temporary hide. Persist the status surface off and do not leave
        // a restore affordance or hover target behind. Restore explicitly via canvas View > Show status bar.
        _settings.ShowStatusSurface = false;
        _statusCollapsed = false;
        _statusSessionClosed = false;
        SaveSettingsAndPublish();
        ApplyStatusVisibility();
        _diagnostics.Write("status", "closed", new { persisted = true });
    }

    private void AutoDismissTransientPanels(object? sender, PointerPressedEventArgs e)
    {
        if (!TransparencyPanel.IsVisible) return;
        if (IsWithinControl(e.Source, TransparencyPanel)) return;
        if (_titleBarButtons.TryGetValue("window.transparency", out var transparencyButton) && IsWithinControl(e.Source, transparencyButton)) return;
        TransparencyPanel.IsVisible = false;
        _diagnostics.Write("window", "transparency_panel_auto_dismissed", new { reason = "outside_pointer_press" });
    }

    private static bool IsWithinControl(object? source, Control ancestor)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void TransparencyClicked(object? sender, RoutedEventArgs e)
    {
        TransparencyPanel.IsVisible = !TransparencyPanel.IsVisible;
        TransparencySlider.Value = Math.Round(_sessionOpacity * 100);
        TransparencyValueText.Text = $"{(int)Math.Round(_sessionOpacity * 100)}%";
        if (TransparencyPanel.IsVisible) TransparencySlider.Focus();
        _diagnostics.Write("window", "transparency_panel_toggled", new { visible = TransparencyPanel.IsVisible, percent = (int)Math.Round(_sessionOpacity * 100) });
    }

    private void TransparencyResetClicked(object? sender, RoutedEventArgs e)
    {
        TransparencySlider.Value = 100;
        TransparencyPanel.IsVisible = false;
        _diagnostics.Write("window", "transparency_reset", new { percent = 100 });
    }

    private bool ActiveAlwaysOnTop => _wholeAppOverlayMode ? _settings.OverlayAlwaysOnTop : _settings.AlwaysOnTop;
    private bool HardAlwaysOnTop => !string.Equals(_settings.AlwaysOnTopMode, "Soft", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeOverlayPlacementKey(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private void SaveCurrentWholeAppOverlayPlacement(string? path = null)
    {
        if (!_wholeAppOverlayMode || _applyingWholeAppOverlayPlacement || WindowState != WindowState.Normal || Bounds.Width < 80 || Bounds.Height < 80) return;
        path ??= _wholeAppOverlayPlacementPath ?? _currentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var key = NormalizeOverlayPlacementKey(path);
        _settings.OverlayWindowPlacements ??= new Dictionary<string, OverlayWindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        _settings.OverlayWindowPlacements[key] = new OverlayWindowPlacementState
        {
            X = Position.X,
            Y = Position.Y,
            Width = Bounds.Width,
            Height = Bounds.Height,
            LastUsedUtcTicks = DateTime.UtcNow.Ticks
        };
        // Prevent a long browsing history from turning the settings file into a file index.
        if (_settings.OverlayWindowPlacements.Count > 128)
        {
            foreach (var stale in _settings.OverlayWindowPlacements.OrderBy(kv => kv.Value.LastUsedUtcTicks).Take(_settings.OverlayWindowPlacements.Count - 128).Select(kv => kv.Key).ToArray())
                _settings.OverlayWindowPlacements.Remove(stale);
        }
    }

    private void ApplyWholeAppOverlayPlacementForCurrentImage(bool preferRemembered = true)
    {
        if (!_wholeAppOverlayMode || string.IsNullOrWhiteSpace(_currentPath)) return;
        var path = _currentPath;
        var key = NormalizeOverlayPlacementKey(path);
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        var screens = Screens.All;
        var remembered = preferRemembered && _settings.OverlayWindowPlacements is not null && _settings.OverlayWindowPlacements.TryGetValue(key, out var placement)
            ? placement : null;

        double width;
        double height;
        PixelPoint position;
        if (remembered is not null && remembered.Width >= 80 && remembered.Height >= 80 &&
            IsSafeSavedWindowPosition(new PixelPoint(remembered.X, remembered.Y), remembered.Width, remembered.Height, scale))
        {
            width = remembered.Width;
            height = remembered.Height;
            position = new PixelPoint(remembered.X, remembered.Y);
        }
        else
        {
            var screen = screens.FirstOrDefault(x => Position.X >= x.WorkingArea.X && Position.X < x.WorkingArea.Right && Position.Y >= x.WorkingArea.Y && Position.Y < x.WorkingArea.Bottom) ?? Screens.Primary ?? screens.FirstOrDefault();
            if (screen is null) return;
            var area = screen.WorkingArea;
            var sourceW = Math.Max(1, _currentPixelWidth > 0 ? _currentPixelWidth : Viewport.Bitmap?.PixelSize.Width ?? 1);
            var sourceH = Math.Max(1, _currentPixelHeight > 0 ? _currentPixelHeight : Viewport.Bitmap?.PixelSize.Height ?? 1);
            var aspect = sourceW / (double)sourceH;
            // Medium/small by default, while guaranteeing that the transparent window itself has the image aspect ratio.
            var maxWidthDip = Math.Max(240.0, area.Width / scale * 0.55);
            var maxHeightDip = Math.Max(180.0, area.Height / scale * 0.55);
            width = Math.Min(maxWidthDip, maxHeightDip * aspect);
            height = width / aspect;
            if (height > maxHeightDip) { height = maxHeightDip; width = height * aspect; }
            // Never create an unusably tiny portrait/panorama overlay.
            if (width < 240) { width = 240; height = width / aspect; }
            if (height < 160) { height = 160; width = height * aspect; }
            if (width > area.Width / scale * 0.85) { width = area.Width / scale * 0.85; height = width / aspect; }
            if (height > area.Height / scale * 0.85) { height = area.Height / scale * 0.85; width = height * aspect; }
            var widthPx = Math.Max(1, (int)Math.Round(width * scale));
            var heightPx = Math.Max(1, (int)Math.Round(height * scale));
            position = new PixelPoint(area.X + (area.Width - widthPx) / 2, area.Y + (area.Height - heightPx) / 2);
        }

        _applyingWholeAppOverlayPlacement = true;
        try
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Width = Math.Max(80, width);
            Height = Math.Max(80, height);
            Position = position;
            _wholeAppOverlayPlacementPath = path;
            Viewport.Fit();
            Dispatcher.UIThread.Post(AlignWholeAppOverlayChromeToPixels, DispatcherPriority.Render);
        }
        finally { _applyingWholeAppOverlayPlacement = false; }
    }

    private void ApplyTopmostState(bool forceNative = false)
    {
        var active = ActiveAlwaysOnTop;
        Topmost = active;
        _overlays?.SetTopmost(active);
        if (_wholeAppOverlayChromeVisual is { } overlayChrome)
        {
            overlayChrome.TopmostActive = active;
            overlayChrome.InvalidateVisual();
        }
        if (active && HardAlwaysOnTop && _topmostTransientDepth == 0)
        {
            if (!_hardTopmostTimer.IsEnabled) _hardTopmostTimer.Start();
            if (forceNative) ReassertNativeTopmost();
        }
        else if (_hardTopmostTimer.IsEnabled)
            _hardTopmostTimer.Stop();
    }

    private void ReassertNativeTopmost()
    {
        if (!OperatingSystem.IsWindows() || !ActiveAlwaysOnTop || !HardAlwaysOnTop || _topmostTransientDepth > 0 || !IsVisible || WindowState == WindowState.Minimized) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        // Another (or this) window is inside Windows' modal move/size loop: re-raising now makes the
        // windows fight over z-order and re-enters WM_WINDOWPOSCHANGED mid-drag. Resume afterwards.
        if (Volatile.Read(ref s_nativeMoveLoopDepth) > 0) return;
        // Menus, submenus and tooltips are separate no-activate topmost popup HWNDs on the UI thread.
        // Re-raising this window while one is open would bury it, so back off until it closes.
        if (HasOpenPopupWindow(hwnd)) return;
        try
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
        }
        catch { }
    }


    private static int s_nativeMoveLoopDepth;
    private bool _countedNativeMoveLoop;

    private void EnterNativeMoveLoopGuard()
    {
        if (_countedNativeMoveLoop) return;
        _countedNativeMoveLoop = true;
        Interlocked.Increment(ref s_nativeMoveLoopDepth);
    }

    private void LeaveNativeMoveLoopGuard()
    {
        if (!_countedNativeMoveLoop) return;
        _countedNativeMoveLoop = false;
        Interlocked.Decrement(ref s_nativeMoveLoopDepth);
        foreach (var w in GlideWindowRegistry.Snapshot()) w.ReassertNativeTopmost();
    }

    private delegate bool EnumThreadWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLongExStyle(IntPtr hwnd, int index);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static bool HasOpenPopupWindow(IntPtr self)
    {
        const int GwlExStyle = -20, WsExTopmost = 0x8, WsExNoActivate = 0x08000000;
        var found = false;
        try
        {
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                var ex = GetWindowLongExStyle(h, GwlExStyle);
                if ((ex & WsExTopmost) != 0 && (ex & WsExNoActivate) != 0) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private IDisposable EnterTopmostTransient()
    {
        _topmostTransientDepth++;
        if (_hardTopmostTimer.IsEnabled) _hardTopmostTimer.Stop();
        return new TopmostTransientScope(this);
    }

    private void LeaveTopmostTransient()
    {
        if (_topmostTransientDepth > 0) _topmostTransientDepth--;
        if (_topmostTransientDepth == 0) ApplyTopmostState(forceNative: true);
    }

    private sealed class TopmostTransientScope : IDisposable
    {
        private MainWindow? _owner;
        public TopmostTransientScope(MainWindow owner) => _owner = owner;
        public void Dispose() { var owner = Interlocked.Exchange(ref _owner, null); owner?.LeaveTopmostTransient(); }
    }

    private ContextMenu PrepareOwnedContextMenu(ContextMenu menu)
    {
        IDisposable? scope = null;
        menu.Opened += (_, _) => { scope ??= EnterTopmostTransient(); _activeOwnedMenu = menu; };
        menu.Closed += (_, _) =>
        {
            scope?.Dispose(); scope = null;
            if (ReferenceEquals(_activeOwnedMenu, menu)) _activeOwnedMenu = null;
            _ownedMenuClosedTick = Environment.TickCount64;
        };
        // Picking an item can restyle/re-create the window (overlay mode, always-on-top changes);
        // close the popup explicitly afterwards so it never lingers as a ghost.
        menu.AddHandler(MenuItem.ClickEvent, (_, _) => Dispatcher.UIThread.Post(() => CloseActiveOwnedMenu(menu), DispatcherPriority.Background), RoutingStrategies.Bubble, true);
        return menu;
    }

    private ContextMenu? _activeOwnedMenu;
    private long _ownedMenuClosedTick;

    private void CloseActiveOwnedMenu(ContextMenu? only = null)
    {
        var menu = _activeOwnedMenu;
        if (menu is null || (only is not null && !ReferenceEquals(menu, only))) return;
        try { if (menu.IsOpen) menu.Close(); } catch { }
        _activeOwnedMenu = null;
    }

    /// <summary>
    /// Builds a small modal whose height follows its content and can never exceed the owner's
    /// working area. The previous fixed-height prompts clipped their buttons on small or
    /// high-scaling laptop panels (the folder-boundary prompt was the reported case) even though
    /// screen space existed.
    /// </summary>
    private Window CreateContentSizedDialog(string title, double minWidth, double maxWidth)
    {
        var dialog = new Window
        {
            Title = title,
            Icon = Icon,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = minWidth,
            MaxWidth = maxWidth,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        try
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var scaling = Math.Max(1.0, RenderScaling);
                dialog.MaxHeight = Math.Max(220, screen.WorkingArea.Height / scaling - 80);
            }
        }
        catch { }
        return dialog;
    }

    private async Task ShowOwnedDialogAsync(Window dialog)
    {
        // Avalonia refuses ShowDialog with a non-visible owner. Treat that as a safe no-op rather
        // than letting the InvalidOperationException escape an async void caller.
        if (!IsVisible)
        {
            try { dialog.Close(); } catch { }
            return;
        }
        using var scope = EnterTopmostTransient();
        dialog.Topmost = ActiveAlwaysOnTop;
        await dialog.ShowDialog(this);
    }

    private async Task<T> ShowOwnedDialogAsync<T>(Window dialog)
    {
        if (!IsVisible)
        {
            try { dialog.Close(); } catch { }
            return default!;
        }
        using var scope = EnterTopmostTransient();
        dialog.Topmost = ActiveAlwaysOnTop;
        return await dialog.ShowDialog<T>(this);
    }
}
