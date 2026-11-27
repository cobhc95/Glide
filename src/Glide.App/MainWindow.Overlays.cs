using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.Core;

using Glide.App.Services;

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

        // Overlay mode is an exact copy of a picture-in-picture overlay: the same chrome (LegacyOverlayChrome draws the shared
        // OverlayFrameChromePainter from OverlayFrameGeometry) and the same controls: close, opacity pill, zoom buttons, corner
        // grip and the two edge handles. The transparent hit targets below apply those shared regions to the real window
        // (resizing it, closing Glide) without putting this UI on the normal cold-image path. Only the right-click menu and the
        // optional drag strip are Overlay-mode additions.
        var chrome = new LegacyOverlayChrome
        {
            ChromeVisible = true,
            OpacityValue = _sessionOpacity,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
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
        closeHit.Click += (_, _) =>
        {
            // Whole-app Overlay X is a real close affordance, not a mode toggle.
            // If this process owns multiple Glide windows, closing Overlay exits the entire
            // current Glide application/session exactly as requested.
            // It must not divert into Speed Boost standby: before 5.0.8 the forced shutdown's Closing
            // handler put the window into standby, which kept the broker acknowledging launches while
            // the process exited, so the next launch forwarded to a dying process and vanished.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
                App.ShutdownApplication("whole_app_overlay_close");
            else
                Close();
        };

        // Opacity grab area. Not an Avalonia Slider: its own track/thumb geometry differed from the drawn pill,
        // so the dot lagged the pointer, a click paged the value and the 1 px thumb was hard to grab (round 3 #22).
        // Press anywhere on the pill sets the value under the pointer, dragging follows it, the wheel steps it (#21).
        var opacityHit = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Focusable = false, ZIndex = 2
        };
        opacityHit.Classes.Add(InteractiveHitClass);
        IPointer? opacityDrag = null;
        void SetFromPointer(PointerEventArgs e)
        {
            if (_wholeAppOverlayControlsHost is not { } controlsHost) return;
            var slider = OverlayChromePolicy.SliderRect(controlsHost.Bounds.Size);
            SetWholeAppOverlayOpacity(OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, e.GetPosition(controlsHost).Y));
        }
        opacityHit.PointerPressed += (_, e) =>
        {
            if (!_wholeAppOverlayMode || !e.GetCurrentPoint(opacityHit).Properties.IsLeftButtonPressed) return;
            opacityDrag = e.Pointer;
            e.Pointer.Capture(opacityHit);
            SetFromPointer(e);
            e.Handled = true;
        };
        opacityHit.PointerMoved += (_, e) =>
        {
            if (opacityDrag is null || !ReferenceEquals(e.Pointer, opacityDrag)) return;
            if (!e.GetCurrentPoint(opacityHit).Properties.IsLeftButtonPressed) { opacityDrag = null; e.Pointer.Capture(null); return; }
            SetFromPointer(e);
            e.Handled = true;
        };
        opacityHit.PointerReleased += (_, e) =>
        {
            if (opacityDrag is null) return;
            opacityDrag = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        opacityHit.PointerCaptureLost += (_, _) => opacityDrag = null;
        // No wheel handler: like a picture-in-picture overlay, the wheel over the pill zooms the picture.

        Button ZoomHit(bool zoomIn)
        {
            var button = new Button
            {
                // Placed on the picture-in-picture zoom button rectangles when the host is laid out (see host.SizeChanged).
                Width = 30, Height = 30,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Opacity = 0.01, Focusable = false, ZIndex = 2
            };
            button.Click += (_, _) => Viewport.ZoomStep(zoomIn);
            return button;
        }
        var zoomOutHit = ZoomHit(false);
        var zoomInHit = ZoomHit(true);
        // The corner grip and the two edge handles of a picture-in-picture overlay, resizing the real window. (Windows' own
        // border resize still works as well.)
        var gripHit = CreateWholeAppOverlayResizeHit(OverlayResizeMode.Proportional);
        var widthHit = CreateWholeAppOverlayResizeHit(OverlayResizeMode.WidthOnly);
        var heightHit = CreateWholeAppOverlayResizeHit(OverlayResizeMode.HeightOnly);

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
        _wholeAppOverlayDragBand = dragBand;

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
        host.Children.Add(gripHit);
        host.Children.Add(widthHit);
        host.Children.Add(heightHit);
        host.SizeChanged += (_, _) =>
        {
            var size = host.Bounds.Size;
            var frame = new Rect(default, size);
            // Only the controls that fit a frame this small are drawn (OverlayFrameGeometry.PartsFor); their hit areas follow.
            var parts = OverlayFrameGeometry.PartsFor(size);
            void Place(Control control, Rect rect, bool shown)
            {
                // Hit areas never reach outside the window (the corner grip's hit box overhangs the frame by 4 DIP). A hidden control
                // keeps a zero-size box (not IsVisible=false: a handle that is being dragged keeps its pointer capture).
                rect = shown ? rect.Intersect(frame) : default;
                control.Margin = new Thickness(rect.X, rect.Y, 0, 0);
                control.Width = rect.Width;
                control.Height = rect.Height;
            }
            closeHit.IsVisible = parts.Close;
            Place(opacityHit, OverlayFrameGeometry.Slider(frame), parts.Slider);
            Place(zoomOutHit, OverlayFrameGeometry.ZoomOut(frame), parts.Zoom);
            Place(zoomInHit, OverlayFrameGeometry.ZoomIn(frame), parts.Zoom);
            Place(gripHit, OverlayFrameGeometry.GripHit(frame), parts.Grip);
            Place(widthHit, OverlayFrameGeometry.WidthHandle(frame), parts.WidthHandle);
            Place(heightHit, OverlayFrameGeometry.HeightHandle(frame), parts.HeightHandle);
        };
        WorkspaceLayer.Children.Add(host);

        _wholeAppOverlayChromeVisual = chrome;
        _wholeAppOverlayControlsHost = host;
        return host;
    }

    // ---- corner grip and edge handles: resize the real window like a picture-in-picture overlay ------------------

    private sealed class WholeAppOverlayResizeSession
    {
        public OverlayResizeMode Mode { get; }
        public IPointer Pointer { get; }
        public PixelPoint CursorStart { get; }
        public Size StartSize { get; }
        public PixelPoint StartPosition { get; }
        public double Scale { get; }
        public bool Crop { get; }
        public Size CropView { get; }
        public Size CropImage { get; }
        public OverlayResizeStage Stage { get; }
        public Avalonia.Layout.HorizontalAlignment SavedImageHAlign { get; }
        public Avalonia.Layout.VerticalAlignment SavedImageVAlign { get; }
        public double SavedImageWidth { get; }
        public double SavedImageHeight { get; }
        public Thickness SavedImageMargin { get; }
        public PixelPoint CurrentOrigin { get; set; }
        public Size CurrentSize { get; set; }
        public OverlayResizeSyncTrace Trace { get; }
        public long StartRenders { get; }
        public long StartRequestId { get; }
        public Avalonia.Media.Imaging.Bitmap? StartBitmap { get; }

        public WholeAppOverlayResizeSession(OverlayResizeMode mode, IPointer pointer, PixelPoint cursorStart, Size startSize, PixelPoint startPosition,
            double scale, bool crop, Size cropView, Size cropImage, OverlayResizeStage stage,
            Avalonia.Layout.HorizontalAlignment savedHAlign, Avalonia.Layout.VerticalAlignment savedVAlign,
            double savedWidth, double savedHeight, Thickness savedMargin,
            OverlayResizeSyncTrace trace, long startRenders, long startRequestId, Avalonia.Media.Imaging.Bitmap? startBitmap)
        {
            Mode = mode;
            Pointer = pointer;
            CursorStart = cursorStart;
            StartSize = startSize;
            StartPosition = startPosition;
            Scale = scale;
            Crop = crop;
            CropView = cropView;
            CropImage = cropImage;
            Stage = stage;
            SavedImageHAlign = savedHAlign;
            SavedImageVAlign = savedVAlign;
            SavedImageWidth = savedWidth;
            SavedImageHeight = savedHeight;
            SavedImageMargin = savedMargin;
            CurrentOrigin = startPosition;
            CurrentSize = startSize;
            Trace = trace;
            StartRenders = startRenders;
            StartRequestId = startRequestId;
            StartBitmap = startBitmap;
        }
    }

    private WholeAppOverlayResizeSession? _wholeAppOverlayResize;

    /// <summary>Overlay mode's "Preserve aspect ratio" (the same switch as a picture-in-picture overlay's; on by default, per session).
    /// On: the corner keeps the ratio and the edge handles crop. Off: the corner and the edges stretch the picture to fill the window.</summary>
    private bool _wholeAppOverlayPreserveAspect = true;

    private void ApplyWholeAppOverlayAspect() =>
        Viewport.StretchToFill = _wholeAppOverlayMode && !_wholeAppOverlayPreserveAspect;

    private void ToggleWholeAppOverlayPreserveAspect()
    {
        _wholeAppOverlayPreserveAspect = !_wholeAppOverlayPreserveAspect;
        if (!_wholeAppOverlayPreserveAspect) Viewport.Fit(); // a frozen edge-crop zoom would otherwise keep the picture uniform
        ApplyWholeAppOverlayAspect();
        Dispatcher.UIThread.Post(AlignWholeAppOverlayChromeToPixels, DispatcherPriority.Render);
        if (_wholeAppOverlayControlsHost is { } controls) controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
    }

    /// <summary>True while a grip / handle drag resizes the Overlay window (the chrome stays up, like a picture-in-picture resize).</summary>
    private bool WholeAppOverlayResizing => _wholeAppOverlayResize is not null;

    private Border CreateWholeAppOverlayResizeHit(OverlayResizeMode mode)
    {
        var hit = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Focusable = false, ZIndex = 2
        };
        hit.Classes.Add(InteractiveHitClass);
        hit.PointerPressed += (_, e) => BeginWholeAppOverlayResize(mode, hit, e);
        hit.PointerMoved += (_, e) => UpdateWholeAppOverlayResize(e);
        hit.PointerReleased += (_, e) => { if (EndWholeAppOverlayResize(e.Pointer)) e.Handled = true; };
        hit.PointerCaptureLost += (_, e) => EndWholeAppOverlayResize(e.Pointer);
        return hit;
    }

    private (double MaxWidth, double MaxHeight) WholeAppOverlayResizeMaxBounds(PixelPoint startPosition, Size startSize, double scale,
        bool crop, Size cropView, Size cropImage)
    {
        double maxWidth = double.PositiveInfinity, maxHeight = double.PositiveInfinity;
        var box = new PixelBox(startPosition.X, startPosition.Y,
            (int)Math.Round(startSize.Width * scale), (int)Math.Round(startSize.Height * scale));
        if (WholeAppOverlayScreenFor(box) is { } screen)
        {
            maxWidth = screen.WorkingArea.Width / scale;
            maxHeight = screen.WorkingArea.Height / scale;
        }
        if (crop)
        {
            // Like picture-in-picture: the window can grow back only to the full picture, never past its edge.
            maxWidth = Math.Min(maxWidth, startSize.Width + Math.Max(0, cropImage.Width - cropView.Width));
            maxHeight = Math.Min(maxHeight, startSize.Height + Math.Max(0, cropImage.Height - cropView.Height));
        }
        return (maxWidth, maxHeight);
    }

    private void BeginWholeAppOverlayResize(OverlayResizeMode mode, Control hit, PointerPressedEventArgs e)
    {
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || !e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        // The top handle moves the window's top edge: with Lock position on it (like every drag) does nothing.
        if (WindowState != WindowState.Normal || (mode == OverlayResizeMode.HeightOnly && WholeAppOverlayPositionLocked)) return;
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        // The edge handles crop like picture-in-picture: the picture keeps its size and zoom, only the visible edge moves.
        Size cropView = default, cropImage = default;
        var crop = mode != OverlayResizeMode.Proportional && _wholeAppOverlayPreserveAspect && Viewport.BeginEdgeCrop(out cropView, out cropImage);
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var startSize = new Size(double.IsNaN(Width) ? Bounds.Width : Width, double.IsNaN(Height) ? Bounds.Height : Height);
        var startPos = Position;
        var (maxWidth, maxHeight) = WholeAppOverlayResizeMaxBounds(startPos, startSize, scale, crop, cropView, cropImage);
        var stage = OverlayResizeStage.Compute(mode, startPos, startSize, maxWidth, maxHeight, scale);
        _wholeAppOverlayResize = new WholeAppOverlayResizeSession(mode, e.Pointer, this.PointToScreen(e.GetPosition(this)),
            startSize, startPos, scale, crop, cropView, cropImage, stage,
            ImageView.HorizontalAlignment, ImageView.VerticalAlignment, ImageView.Width, ImageView.Height, ImageView.Margin,
            new OverlayResizeSyncTrace(), Viewport.RenderCount, Volatile.Read(ref _imageRequestId), Viewport.Bitmap);
        // A resize drag only transforms the bitmap that is already on screen: cheap sampling while it runs, one full-quality draw and one
        // sufficiency check when it ends. The window region (click-through) is suspended: it is re-applied once at the end, because a
        // region that changes with every size step makes DWM compose an old region with a new frame (ghost / spill past the border).
        Viewport.BeginResizeQuality();
        UpdateWholeAppOverlayInputRegion();
        EnsureOverlayWndProcHook();
        if (stage.Active)
        {
            // Pin ImageView to its exact current screen rect inside the transparent stage HWND. Expanding the HWND once up front
            // avoids calling SetWindowPos on every drag frame (which in RedirectionSurface mode makes DWM clip the previous
            // redirection bitmap against the new HWND bounds before Avalonia's async render thread paints the new size).
            ImageView.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            ImageView.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            ImageView.Width = startSize.Width;
            ImageView.Height = startSize.Height;
            ImageView.Margin = stage.ContentMargin(startPos, scale);
            if (stage.Origin != startPos || Math.Abs(stage.Size.Width - startSize.Width) > 0.5 || Math.Abs(stage.Size.Height - startSize.Height) > 0.5)
            {
                ApplyWholeAppOverlayResizeBox(stage.Origin, stage.Size.Width, stage.Size.Height, scale, preserveBits: stage.Origin == startPos);
                UpdateLayout();
                AlignWholeAppOverlayChromeToPixels();
                UpdateLayout();
            }
        }
        e.Pointer.Capture(hit);
        _diagnostics.Write("input", "overlay_window_resize_begin", new { mode = mode.ToString(), Width, Height, staged = stage.Active });
    }

    private bool _overlayWndProcHookInstalled;

    /// <summary>Watches WM_SIZE / WM_PAINT of the main window to count the unpainted steps of an Overlay resize drag (diagnostics only); every other message costs one field read.</summary>
    private void EnsureOverlayWndProcHook()
    {
        if (_overlayWndProcHookInstalled || !OperatingSystem.IsWindows()) return;
        _overlayWndProcHookInstalled = true;
        try { Win32Properties.AddWndProcHookCallback(this, OverlayWndProcHook); }
        catch (Exception ex) { _overlayWndProcHookInstalled = false; _diagnostics.Write("window", "overlay_wndproc_hook_failed", new { ex.Message }); }
    }

    private IntPtr OverlayWndProcHook(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wholeAppOverlayResize?.Trace is not { } trace) return IntPtr.Zero;
        const uint WmSize = 0x0005, WmPaint = 0x000F;
        if (message == WmSize) trace.OnSize();
        else if (message == WmPaint) trace.OnPaint();
        return IntPtr.Zero;
    }

    private void UpdateWholeAppOverlayResize(PointerEventArgs e)
    {
        if (_wholeAppOverlayResize is not { } session || !ReferenceEquals(e.Pointer, session.Pointer)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndWholeAppOverlayResize(e.Pointer); return; }
        e.Handled = true;
        var scale = session.Scale;
        var cursor = this.PointToScreen(e.GetPosition(this));
        var dx = (cursor.X - session.CursorStart.X) / scale;
        var dy = (cursor.Y - session.CursorStart.Y) / scale;
        // Largest window: the work area of the screen the window is on (and full picture edge when cropping).
        var (maxWidth, maxHeight) = WholeAppOverlayResizeMaxBounds(session.StartPosition, session.StartSize, scale,
            session.Crop, session.CropView, session.CropImage);
        // Ratio on: the corner keeps the ratio it had when grabbed. Ratio off: every handle stretches freely.
        var size = OverlayResizeMath.Compute(session.Mode, session.StartSize.Width, session.StartSize.Height, dx, dy,
            _wholeAppOverlayPreserveAspect, maxWidth, maxHeight);
        var newWidth = Math.Max(MinWidth, size.Width);
        var newHeight = Math.Max(MinHeight, size.Height);
        var origin = session.Mode == OverlayResizeMode.HeightOnly
            ? new PixelPoint(session.StartPosition.X, session.StartPosition.Y + (int)Math.Round(size.TopShift * scale))
            : session.StartPosition;
        // Pointer moves arrive faster than frames (1000 Hz mice): only the newest target is kept and it is applied once per frame.
        _wholeAppOverlayResizePending = new PendingOverlayResize(origin, newWidth, newHeight, scale,
            session.Crop ? new Size(session.CropView.Width + (newWidth - session.StartSize.Width),
                session.CropView.Height + (newHeight - session.StartSize.Height)) : null,
            session.Mode == OverlayResizeMode.WidthOnly);
        if (_wholeAppOverlayResizeFrameQueued) return;
        _wholeAppOverlayResizeFrameQueued = true;
        RequestAnimationFrame(_ => { _wholeAppOverlayResizeFrameQueued = false; FlushWholeAppOverlayResize(); });
    }

    private sealed record PendingOverlayResize(PixelPoint Origin, double Width, double Height, double Scale, Size? CropViewSize, bool Horizontal);

    private PendingOverlayResize? _wholeAppOverlayResizePending;
    private bool _wholeAppOverlayResizeFrameQueued;

    /// <summary>
    /// Moves and sizes the window in ONE native SetWindowPos. Setting Position, Width and Height separately made Windows place the
    /// window up to three times per pointer move (the top handle moved the top edge first and resized after, so the picture jumped
    /// and flickered while dragging); one call shows exactly one frame per move.
    /// </summary>
    private void ApplyWholeAppOverlayResizeBox(PixelPoint origin, double width, double height, double scale, bool preserveBits = false)
    {
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle && handle.Handle != IntPtr.Zero)
        {
            const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoCopyBits = 0x0100, SwpNoOwnerZOrder = 0x0200;
            var flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | (preserveBits ? 0u : SwpNoCopyBits);
            try
            {
                if (Platform.Native.User32.SetWindowPos(handle.Handle, IntPtr.Zero, origin.X, origin.Y,
                        (int)Math.Round(width * scale), (int)Math.Round(height * scale), flags))
                    return;
            }
            catch { /* native call unavailable; fall back to the Avalonia properties below */ }
        }
        if (origin != Position) Position = origin;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Applies the newest pending resize target: edge-crop pan first, then the staged ImageView box (keeping the transparent
    /// stage HWND fixed so DWM never composites a resized HWND against a stale redirection bitmap), or a fallback SetWindowPos
    /// when unstaged, then layout, then chrome alignment and layout in the same pass.
    /// </summary>
    private void FlushWholeAppOverlayResize()
    {
        if (_wholeAppOverlayResizePending is not { } p) return;
        _wholeAppOverlayResizePending = null;
        if (_wholeAppOverlayResize is not { } session) return;
        // One frame, in this order: crop pan -> staged ImageView box (no HWND resize during the drag) -> layout (the viewport now
        // has its new bounds and picture rect) -> chrome and picture frame from that rect -> layout of the chrome. Both the image
        // and the chrome paint atomically in the same Avalonia compositor pass on the unchanged redirection surface.
        if (p.CropViewSize is { } cropView) Viewport.UpdateEdgeCrop(cropView, p.Horizontal);
        if (session.Stage.Active)
        {
            ImageView.Width = p.Width;
            ImageView.Height = p.Height;
            ImageView.Margin = session.Stage.ContentMargin(p.Origin, p.Scale);
        }
        else
        {
            ApplyWholeAppOverlayResizeBox(p.Origin, p.Width, p.Height, p.Scale);
        }
        session.CurrentOrigin = p.Origin;
        session.CurrentSize = new Size(p.Width, p.Height);
        UpdateLayout();
        AlignWholeAppOverlayChromeToPixels();
        UpdateLayout();
        session.Trace.OnFlush();
    }

    /// <summary>The title-bar / status-bar / tab-strip sizing that SizeChanged skips on every step of an Overlay resize, run once at the end.</summary>
    private void ApplyChromeSizingAfterWholeAppOverlayResize()
    {
        ApplyStatusBarSize();
        if (_tabDrag.IsActive || _physicalDraggedTabId is not null || Math.Abs(Bounds.Width - _lastTabLayoutWidth) < 24) return;
        _lastTabLayoutWidth = Bounds.Width;
        if (!TryResizeTabStripInPlace()) RebuildTabStrip();
    }

    private bool EndWholeAppOverlayResize(IPointer pointer)
    {
        if (_wholeAppOverlayResize is not { } session || !ReferenceEquals(pointer, session.Pointer)) return false;
        FlushWholeAppOverlayResize();
        if (session.Stage.Active)
        {
            // Restore ImageView to fill the window and commit the final tight HWND box in one native SetWindowPos.
            ImageView.HorizontalAlignment = session.SavedImageHAlign;
            ImageView.VerticalAlignment = session.SavedImageVAlign;
            ImageView.Width = session.SavedImageWidth;
            ImageView.Height = session.SavedImageHeight;
            ImageView.Margin = session.SavedImageMargin;
            ApplyWholeAppOverlayResizeBox(session.CurrentOrigin, session.CurrentSize.Width, session.CurrentSize.Height, session.Scale,
                preserveBits: session.CurrentOrigin == session.Stage.Origin);
        }
        _wholeAppOverlayResize = null;
        // The one full-quality draw of the drag, then the chrome and the window region (click-through) for the final layout.
        Viewport.EndResizeQuality();
        ApplyChromeSizingAfterWholeAppOverlayResize();
        if (session.Crop) Viewport.EndEdgeCrop();
        try { pointer.Capture(null); } catch { /* pointer capture already released; nothing to undo */ }
        try { TrackNormalWindowPlacement(); SaveCurrentWholeAppOverlayPlacement(force: true); }
        catch (Exception ex) { _diagnostics.Write("window", "overlay_resize_end_failed", new { ex.Message }); }
        ScheduleResizeInputReset();
        UpdateLayout();
        AlignWholeAppOverlayChromeToPixels();
        Dispatcher.UIThread.Post(AlignWholeAppOverlayChromeToPixels, DispatcherPriority.Render);
        // Decodes and viewport demand were held back while the drag ran: one check now, with the final size.
        PublishViewportDemand();
        EnsurePresentedFrameSufficient("overlay_resize_end");
        // Steps that were superseded before any WM_PAINT presented them are the frames the compositor shows stretched or cropped.
        var t = session.Trace;
        _diagnostics.Write("input", "overlay_window_resize_end", new
        {
            mode = session.Mode.ToString(), Width, Height, staged = session.Stage.Active,
            sizeSteps = t.Steps, paintedSteps = t.Paints, unpaintedSteps = t.UnpaintedSteps, unpaintedPercent = t.UnpaintedPercent,
            // Cost of the drag: the retained bitmap is only redrawn at its new transform (viewportRenders ~ frames), nothing is decoded
            // or re-uploaded (fullRendersOrDecodes should be 0), the region is cut once (suspended) and the chrome aligned once per frame.
            viewportRenders = Math.Max(0, Viewport.RenderCount - session.StartRenders),
            presentRequests = OverlayResizeSyncTrace.CountDelta(Volatile.Read(ref _imageRequestId), session.StartRequestId),
            bitmapSwaps = ReferenceEquals(session.StartBitmap, Viewport.Bitmap) ? 0 : 1,
            deferredRefineChecks = t.DeferredRefines,
            fullRendersOrDecodes = OverlayResizeSyncTrace.CountDelta(Volatile.Read(ref _imageRequestId), session.StartRequestId) + (ReferenceEquals(session.StartBitmap, Viewport.Bitmap) ? 0 : 1),
            regionApplies = t.RegionApplies, chromeAligns = t.ChromeAligns, frames = t.Flushes
        });
        return true;
    }

    /// <summary>Sets the whole-app Overlay opacity (clamped to 35-100 %, rounded to whole percent).</summary>
    private void SetWholeAppOverlayOpacity(double value)
    {
        var clamped = OverlayChromePolicy.ClampWholeWindowOpacity(value);
        TransparencySlider.Value = Math.Round(clamped * 100.0, 1);
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

    // ---- input region: only the visible frame takes mouse input --------------------------------------------------

    private IReadOnlyList<RegionRect>? _wholeAppOverlayAppliedRegion;
    private bool _wholeAppOverlayRegionQueued;

    /// <summary>Re-aligns the chrome and the window region on the next render pass (coalesced; safe to call from any layout-changing event).</summary>
    private void QueueWholeAppOverlayRegionUpdate()
    {
        // A resize drag aligns the chrome itself every frame and re-applies the region once when it ends.
        if (!_wholeAppOverlayMode || _wholeAppOverlayRegionQueued || WholeAppOverlayResizing) return;
        _wholeAppOverlayRegionQueued = true;
        Dispatcher.UIThread.Post(() => { _wholeAppOverlayRegionQueued = false; AlignWholeAppOverlayChromeToPixels(); }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Overlay mode's window is a transparent top-level surface that is usually larger than what is visible (a smaller picture inside it,
    /// zoomed out, letterbox). Windows hands every click on the whole HWND to Glide, so the see-through part swallowed clicks meant for the
    /// windows beneath (WM_NCHITTEST / HTTRANSPARENT cannot help: it only reaches windows of the same thread). The window region is cut
    /// to the visible frame: the picture (the chrome, handles and drag strip all lie inside it) plus the picture-in-picture overlays
    /// inside the window. Outside the region the window does not exist for hit testing or painting. No region (the whole window) when the
    /// picture fills the window, when nothing is laid out, and while an overlay drifts by itself; cleared when Overlay mode ends.
    /// </summary>
    private void UpdateWholeAppOverlayInputRegion()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return;
        try
        {
            IReadOnlyList<RegionRect>? wanted = null;
            // While a resize handle is dragged the region is suspended (wanted stays null: the whole window, set once at the start of
            // the drag) and re-applied once when the drag ends; it is also what keeps the compositor from combining an old region with
            // a frame of the new size.
            if (_wholeAppOverlayMode && WindowState == WindowState.Normal && !WholeAppOverlayResizing)
            {
                var frames = new List<Rect>();
                if (TryGetWholeAppOverlayPixelRect(out var picture) && WorkspaceLayer.TranslatePoint(picture.TopLeft, this) is { } pictureOrigin)
                    frames.Add(new Rect(pictureOrigin, picture.Size));
                if (_overlays is { HasOverlays: true } overlays)
                {
                    if (overlays.IsAnimating) frames.Clear();   // drifting windows move without a UI-thread event: do not clip them
                    else
                        foreach (var frame in overlays.FrameRects())
                            if (ImageView.TranslatePoint(frame.TopLeft, this) is { } origin) frames.Add(new Rect(origin, frame.Size));
                }
                var scale = RenderScaling > 0 ? RenderScaling : 1.0;
                wanted = OverlayInputRegion.Compute(frames, scale, (int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale));
            }
            if (OverlayInputRegion.Same(_wholeAppOverlayAppliedRegion, wanted)) return;
            if (Platform.OverlayWindowRegion.Apply(handle.Handle, wanted)) _wholeAppOverlayAppliedRegion = wanted;
            _wholeAppOverlayResize?.Trace?.OnRegionApplied();
        }
        catch (Exception ex) { _diagnostics.Write("window", "overlay_region_failed", new { ex.Message }); }
    }

    private void AlignWholeAppOverlayChromeToPixels()
    {
        if (_wholeAppOverlayControlsHost is { } controls && TryGetWholeAppOverlayPixelRect(out var rect))
        {
            // The frame / chrome host IS the picture rectangle (clipped to the window, so no handle can lie outside it).
            rect = OverlayFrameGeometry.HostRect(rect, WorkspaceLayer.Bounds.Size);
            controls.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            controls.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            controls.Width = rect.Width;
            controls.Height = rect.Height;
            controls.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        }
        // The drag strip is placed from the same picture rectangle, so it moves with the chrome.
        if (_wholeAppOverlayDragStrip is { IsVisible: true } && WholeAppOverlayDragStripWanted) RefreshWholeAppOverlayDragStrip();
        _wholeAppOverlayResize?.Trace?.OnChromeAligned();
        UpdateWholeAppOverlayInputRegion();
    }

    private bool IsPointerOverWholeAppOverlayPixels(Point windowPoint)
    {
        if (!TryGetWholeAppOverlayPixelRect(out var rect)) return false;
        var workspacePoint = this.TranslatePoint(windowPoint, WorkspaceLayer);
        return workspacePoint is { } p && rect.Contains(p);
    }

    private void WholeAppOverlayPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // The wheel zooms over every part of the picture, chrome included (as on a picture-in-picture overlay).
        if (!_wholeAppOverlayMode || _settings.WholeAppOverlayUseWindowedInteractions || Viewport.Bitmap is null ||
            (IsInteractiveSource(e.Source) && !IsWholeAppOverlayChromeSource(e.Source))) return;
        var windowPoint = e.GetPosition(this);
        if (!IsPointerOverWholeAppOverlayPixels(windowPoint)) return;
        var viewportPoint = e.GetPosition(Viewport);
        var zoomIn = e.Delta.Y > 0;
        if (_settings.InvertWheelDirection) zoomIn = !zoomIn;
        Viewport.ZoomBy(zoomIn ? Viewport.WheelZoomFactor : 1.0 / Viewport.WheelZoomFactor, viewportPoint);
        AlignWholeAppOverlayChromeToPixels();
        e.Handled = true;
    }

    private void WholeAppOverlayBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        var onHandle = IsOnWholeAppOverlayMoveHandle(e);
        if (_settings.WholeAppOverlayUseWindowedInteractions)
        {
            // Windowed interactions keep normal Glide behaviour on the picture; the drag strip and the bottom bar
            // still move the window (the top frame band has its own handler).
            if (!onHandle) return;
            if (!_settings.WholeAppOverlayLockPosition) BeginOverlayWindowDrag(e);
            e.Handled = true;
            return;
        }
        if (!onHandle && !IsPointerOverWholeAppOverlayPixels(e.GetPosition(this))) return;
        // A left press on the picture always moves the window (zoomed or cropped too); the right-drag pans. Locked: it does neither.
        var decision = OverlayWindowDragPolicy.Resolve(positionLocked: _settings.WholeAppOverlayLockPosition);
        switch (decision)
        {
            case OverlayLeftDrag.MoveWindow:
                BeginOverlayWindowDrag(e);
                e.Handled = true;
                break;
            case OverlayLeftDrag.Nothing:
                e.Handled = true;   // locked: the press must not reach the viewport's pan or window move
                break;
        }
    }

    private bool IsWholeAppOverlayChromeSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
            if (ReferenceEquals(current, _wholeAppOverlayControlsHost)) return true;
        return false;
    }

    /// <summary>The press is on the optional drag strip or the bottom button bar (outside its buttons).</summary>
    private bool IsOnWholeAppOverlayMoveHandle(PointerEventArgs e)
    {
        if (_wholeAppOverlayDragStrip is { IsHitTestVisible: true } strip)
            for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
                if (ReferenceEquals(current, strip)) return true;
        if (_wholeAppOverlayControlsHost is not { IsVisible: true } host) return false;
        var point = e.GetPosition(host);
        return OverlayChromePolicy.BottomBarRect(host.Bounds.Size).Contains(point);
    }

    private void WholeAppOverlayChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        if (!_settings.WholeAppOverlayLockPosition) BeginOverlayWindowDrag(e);
        e.Handled = true;
    }

    // ---- optional drag strip (round 3 #20) ----------------------------------------------------------------------

    private Border? _wholeAppOverlayDragStrip;
    private Border? _wholeAppOverlayDragBand;

    /// <summary>Overlay mode with Lock position on: no drag moves the window.</summary>
    private bool WholeAppOverlayPositionLocked => _wholeAppOverlayMode && _settings.WholeAppOverlayLockPosition;

    private Border EnsureWholeAppOverlayDragStrip()
    {
        if (_wholeAppOverlayDragStrip is { } existing) return existing;
        // Option A from the approved mockup: an inset pill (centred, 28 DIP below the top edge, so it is clear of the top handle and of the
        // window resize band) with a light grip line; not a title bar.
        var grip = new Border
        {
            Width = 44, Height = 4, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0xE6, 0xE9, 0xEE)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var strip = new Border
        {
            Height = OverlayChromePolicy.DragStripDip,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xBF, 0x14, 0x14, 0x16)),
            CornerRadius = new CornerRadius(OverlayChromePolicy.DragStripDip / 2),
            Child = grip,
            Opacity = 0,
            IsHitTestVisible = false,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            ZIndex = 1601
        };

        // No tooltip: its popup is a separate window, so showing it raised PointerExited on the Overlay, which hid the
        // handle and took its hit area away before the press (user report after 1da8428).
        WorkspaceLayer.Children.Add(strip);
        _wholeAppOverlayDragStrip = strip;
        return strip;
    }

    private DispatcherTimer? _dragStripFadeTimer;
    private bool _dragStripPointerOver;
    private long _dragStripLeftTick;

    /// <summary>Tests: the handle's clock (ms); the real one is Environment.TickCount64.</summary>
    internal Func<long>? DragStripClockForTests { get; set; }
    private long DragStripNow => DragStripClockForTests?.Invoke() ?? Environment.TickCount64;

    /// <summary>
    /// Shows the drag handle at once while the pointer is over the Overlay (setting on); after it leaves, the handle
    /// lingers and fades (<see cref="OverlayChromePolicy.DragStripOpacity"/>). Never hides during a window drag.
    /// </summary>
    private void UpdateWholeAppOverlayDragStrip(bool pointerOver)
    {
        var wanted = WholeAppOverlayDragStripWanted;
        if (!wanted && _wholeAppOverlayDragStrip is null) return;
        if (pointerOver) _dragStripPointerOver = true;
        else if (_dragStripPointerOver) { _dragStripPointerOver = false; _dragStripLeftTick = DragStripNow; }
        else if (_dragStripLeftTick == 0) _dragStripLeftTick = DragStripNow - OverlayChromePolicy.DragStripHideDelayMs - OverlayChromePolicy.DragStripFadeMs;
        RefreshWholeAppOverlayDragStrip();
    }

    /// <summary>"Show drag strip" shows the handle on hover anywhere; "top centre" shows it only in the top-centre zone.</summary>
    private bool WholeAppOverlayDragStripWanted =>
        _wholeAppOverlayMode && (_settings.WholeAppOverlayShowDragStrip || _settings.WholeAppOverlayTopCentreGrip);

    /// <summary>The pointer counts as "over" for the handle: anywhere on the picture with the strip on, else the top-centre zone.</summary>
    private bool DragStripPointerOver(Point windowPointer, bool overPixels)
    {
        if (!overPixels) return false;
        if (_settings.WholeAppOverlayShowDragStrip) return true;
        if (!_settings.WholeAppOverlayTopCentreGrip || !TryGetWholeAppOverlayPixelRect(out var rect)) return false;
        return this.TranslatePoint(windowPointer, WorkspaceLayer) is { } p && OverlayChromePolicy.IsInTopCentreGripZone(rect, p);
    }

    private void RefreshWholeAppOverlayDragStrip()
    {
        var wanted = WholeAppOverlayDragStripWanted;
        var strip = EnsureWholeAppOverlayDragStrip();
        var fits = false;
        if (wanted && TryGetWholeAppOverlayPixelRect(out var rect) && OverlayChromePolicy.DragStripRect(rect) is { } pill)
        {
            strip.Width = pill.Width;
            strip.Margin = new Thickness(pill.X, pill.Y, 0, 0);
            fits = true;
        }
        var dragging = _manualWindowDragLeft && _manualWindowDragPointer is not null;
        var opacity = OverlayChromePolicy.DragStripOpacity(wanted, fits, _dragStripPointerOver, dragging, DragStripNow - _dragStripLeftTick);
        strip.IsVisible = wanted;
        strip.Opacity = opacity;
        strip.IsHitTestVisible = opacity > 0;
        var settling = wanted && fits && !_dragStripPointerOver && !dragging && opacity > 0;
        if (!settling) { _dragStripFadeTimer?.Stop(); return; }
        if (_dragStripFadeTimer is null)
        {
            _dragStripFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _dragStripFadeTimer.Tick += (_, _) => RefreshWholeAppOverlayDragStrip();
        }
        if (!_dragStripFadeTimer.IsEnabled) _dragStripFadeTimer.Start();
    }

    private void ToggleWholeAppOverlayDragStrip()
    {
        _settings.WholeAppOverlayShowDragStrip = !_settings.WholeAppOverlayShowDragStrip;
        SaveSettingsAndPublish();
        UpdateWholeAppOverlayDragStrip(_wholeAppOverlayControlsHost?.IsVisible == true);
        if (_wholeAppOverlayControlsHost is { } controls) controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_drag_strip", new { enabled = _settings.WholeAppOverlayShowDragStrip });
    }

    /// <summary>
    /// The viewport properties that depend on the Overlay interaction mode and Lock position (the same values
    /// ApplySettingsVisuals sets), applied without the full settings re-apply.
    /// </summary>
    private void ApplyWholeAppOverlayInteractionSettings()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var useOverlayInteractions = _wholeAppOverlayMode && !_settings.WholeAppOverlayUseWindowedInteractions;
        Viewport.RightDragWindowMoveAllowed = WindowState == WindowState.Normal && !WholeAppOverlayPositionLocked;
        Viewport.BackgroundWindowDragEnabled = IsLeftWindowMoveAllowedForCurrentState() && !WholeAppOverlayPositionLocked;
        Viewport.SelectionClickZoomEnabled = !useOverlayInteractions;
        Viewport.SelectionRightClickZoomOutEnabled = !useOverlayInteractions;
        Viewport.LeftDragMode = useOverlayInteractions
            ? LeftImageDragMode.Pan
            : _inputRouter.ResolveGesture("drag.leftImage", _settings.Gestures) switch
            {
                Glide.Core.Commands.GestureCatalog.Pan => LeftImageDragMode.Pan,
                Glide.Core.Commands.GestureCatalog.MoveWindow => LeftImageDragMode.MoveWindow,
                _ => LeftImageDragMode.Selection
            };
        ApplySelectionBuffer();
        UpdateWholeAppOverlayDragStrip(_wholeAppOverlayControlsHost?.IsVisible == true);
        _diagnostics.Write("window", "whole_app_overlay_interaction_apply", new { ms = sw.Elapsed.TotalMilliseconds });
    }

    private void ToggleWholeAppOverlayLockPosition()
    {
        _settings.WholeAppOverlayLockPosition = !_settings.WholeAppOverlayLockPosition;
        ApplyWholeAppOverlayInteractionSettings();
        SaveSettingsAndPublish();
        if (_wholeAppOverlayControlsHost is { } controls) controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_lock_position", new { locked = _settings.WholeAppOverlayLockPosition });
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
        ApplyWholeAppOverlayAspect();
        // Overlay geometry must be allowed to follow the image even when normal Glide has a tall
        // minimum height for its tab/status UI. Overlay chrome is floating, so smaller dimensions are safe.
        MinWidth = OverlayResizeMath.MinimumWidth;
        MinHeight = OverlayResizeMath.MinimumHeight;

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
        NotifyStatusChromeChanged();
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
        if (_wholeAppOverlayResize is { } activeResize) EndWholeAppOverlayResize(activeResize.Pointer);
        var snapshot = _wholeAppOverlaySnapshot;
        SaveCurrentWholeAppOverlayPlacement(force: true);
        _wholeAppOverlayMode = false;
        UpdateWholeAppOverlayInputRegion();   // mode is off: the window region is cleared, the whole window takes input again
        ApplyWholeAppOverlayAspect();
        _wholeAppOverlayPlacementPath = null;
        _wholeAppOverlaySnapshot = null;

        if (_wholeAppOverlayControlsHost is { } overlayControls)
        {
            overlayControls.IsVisible = false;
            overlayControls.ContextMenu = null;
        }
        if (_wholeAppOverlayDragStrip is { } dragStripOnExit) { dragStripOnExit.IsVisible = false; dragStripOnExit.Opacity = 0; dragStripOnExit.IsHitTestVisible = false; }
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
                // Round 3 #28: the normal window sometimes kept the Overlay size. Restore the position first
                // (moving back to a monitor with another DPI makes Windows rescale the window, which used to
                // land after the size was set), and keep the size/position events of this hand-off out of the
                // remembered normal placement. A deferred check re-applies the size if a late native resize won.
                _applyingWholeAppOverlayPlacement = true;
                try
                {
                    Position = snapshot.Position;
                    Width = Math.Max(MinWidth, snapshot.Width);
                    Height = Math.Max(MinHeight, snapshot.Height);
                }
                finally { _applyingWholeAppOverlayPlacement = false; }
                if (snapshot.RestoreState != WindowState.Maximized) RememberNormalPlacementAfterOverlay(snapshot);
                Dispatcher.UIThread.Post(() => VerifyNormalSizeAfterOverlay(snapshot), DispatcherPriority.Background);
            }
            if (snapshot.RestoreState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }

        ApplySettingsVisuals();
        if (snapshot?.RestoreFullscreen == true && WindowState != WindowState.FullScreen)
        {
            // EnterFullscreen remembers the live bounds as the normal placement; right after the Overlay hand-off those
            // can still be the Overlay geometry (the native resize has not settled), which later made leaving
            // fullscreen return to the Overlay size (#28). The snapshot is already the remembered normal placement.
            _applyingWholeAppOverlayPlacement = true;
            try { EnterFullscreen(); }
            finally { _applyingWholeAppOverlayPlacement = false; }
        }

        _diagnostics.Write("window", "whole_app_overlay_exited", new
        {
            restoredState = WindowState.ToString(), restoredFullscreen = snapshot?.RestoreFullscreen == true,
            Position, Width, Height
        });
    }

    private void RememberNormalPlacementAfterOverlay(WholeAppOverlaySnapshot snapshot)
    {
        _lastNormalWindowPosition = snapshot.Position;
        _lastNormalWindowSize = new Size(Math.Max(MinWidth, snapshot.Width), Math.Max(MinHeight, snapshot.Height));
        _normalPlacementKnown = true;
        _settings.WindowX = snapshot.Position.X;
        _settings.WindowY = snapshot.Position.Y;
        _settings.WindowWidth = _lastNormalWindowSize.Width;
        _settings.WindowHeight = _lastNormalWindowSize.Height;
    }

    /// <summary>After leaving Overlay: if a late native resize/DPI change left the normal window at another size, put it back.</summary>
    private void VerifyNormalSizeAfterOverlay(WholeAppOverlaySnapshot snapshot)
    {
        if (_wholeAppOverlayMode || WindowState != WindowState.Normal || snapshot.RestoreState != WindowState.Normal) return;
        var width = Math.Max(MinWidth, snapshot.Width);
        var height = Math.Max(MinHeight, snapshot.Height);
        if (Math.Abs(Width - width) <= 1 && Math.Abs(Height - height) <= 1 && Position == snapshot.Position) return;
        _diagnostics.Write("window", "whole_app_overlay_exit_size_reapplied", new { Width, Height, width, height });
        _applyingWholeAppOverlayPlacement = true;
        try
        {
            Position = snapshot.Position;
            Width = width;
            Height = height;
        }
        finally { _applyingWholeAppOverlayPlacement = false; }
        RememberNormalPlacementAfterOverlay(snapshot);
    }
    private void UpdateWholeAppOverlayChromeHover(Point pointer)
    {
        if (!_wholeAppOverlayMode || !IsActive || _nativeSizeMoveActive) return;
        var controls = _wholeAppOverlayControlsHost;
        if (controls is null) return;
        if (WholeAppOverlayResizing) { controls.IsVisible = true; return; }   // the resize frame callback aligns the chrome itself
        AlignWholeAppOverlayChromeToPixels();
        var overPixels = IsPointerOverWholeAppOverlayPixels(pointer);
        // Overlay chrome/glow belongs to the rendered pixels, not the transparent top-level window.
        // It appears only while the pointer is actually over visible image pixels.
        // A picture-in-picture overlay keeps its chrome up for the whole resize drag, wherever the pointer goes.
        controls.IsVisible = overPixels || WholeAppOverlayResizing;
        // The top frame band is the move handle of Windowed interactions only. In Overlay interactions the press
        // must reach the picture, so a zoomed picture pans there too (round 3 #20).
        if (_wholeAppOverlayDragBand is { } band) band.IsHitTestVisible = _settings.WholeAppOverlayUseWindowedInteractions;
        UpdateWholeAppOverlayDragStrip(DragStripPointerOver(pointer, overPixels));
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
        // Narrow apply first so the click feels instant; the full ApplySettingsVisuals pass (tabs, chrome, status bar,
        // image re-present) froze the UI noticeably and none of it depends on this toggle.
        ApplyWholeAppOverlayInteractionSettings();
        SaveSettingsAndPublish();
        if (_wholeAppOverlayControlsHost is { } controls)
            controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_interaction_mode", new
        {
            windowed = _settings.WholeAppOverlayUseWindowedInteractions,
            mode = _settings.WholeAppOverlayUseWindowedInteractions ? "windowed" : "overlay"
        });
    }

    private void AppendWholeAppOverlayMenuItems(List<MenuItem> items, bool includeExit = true)
    {
        if (!_wholeAppOverlayMode)
        {
            var enter = MenuActions.Tag(new MenuItem { Header = MenuActions.Header("Enter Overlay (Window-in-Window) Mode", ShortcutForAction("overlay.toggleMode")), Icon = MenuIcon("Overlays") }, "overlay.toggleMode");
            enter.Click += (_, _) => EnterWholeAppOverlayMode();
            items.Add(enter);
            return;
        }

        // A plain on/off item: the tick means "windowed interactions on" and clicking flips it. The old
        // header ticked both states ("Interaction: Overlay mode ✓" / "... Windowed mode ✓"), so a click
        // looked like it did nothing (round 3 #27).
        var interactions = MenuActions.Tag(new MenuItem
        {
            Header = _settings.WholeAppOverlayUseWindowedInteractions
                ? "Use windowed interactions (drag pans, selection zoom)  ✓"
                : "Use windowed interactions (drag pans, selection zoom)",
            Icon = MenuIcon("Mouse")
        }, "setting.overlay.wholeAppWindowedInteractions");
        interactions.Click += (_, _) => ToggleWholeAppOverlayInteractionMode();
        items.Add(interactions);
        var dragStrip = MenuActions.Tag(new MenuItem
        {
            Header = _settings.WholeAppOverlayShowDragStrip ? "Show drag strip  ✓" : "Show drag strip",
            Icon = MenuIcon("Drag")
        }, "setting.overlay.wholeAppShowDragStrip");
        dragStrip.Click += (_, _) => ToggleWholeAppOverlayDragStrip();
        items.Add(dragStrip);
        var lockPosition = MenuActions.Tag(new MenuItem
        {
            Header = _settings.WholeAppOverlayLockPosition ? "Lock position  ✓" : "Lock position",
            Icon = MenuIcon("Move")
        }, "setting.overlay.wholeAppLockPosition");
        lockPosition.Click += (_, _) => ToggleWholeAppOverlayLockPosition();
        items.Add(lockPosition);
        var preserveAspect = MenuActions.Tag(new MenuItem
        {
            Header = _wholeAppOverlayPreserveAspect ? "Preserve aspect ratio  ✓" : "Preserve aspect ratio",
            Icon = MenuIcon("Maximize")
        }, "overlay.preserveAspect");
        preserveAspect.Click += (_, _) => ToggleWholeAppOverlayPreserveAspect();
        items.Add(preserveAspect);
        var sizeMenu = new MenuItem { Header = "Size", Icon = MenuIcon("Maximize") };
        var (sourceWidth, sourceHeight) = WholeAppOverlaySourcePixels();
        var windowBox = CurrentWindowPixelBox();
        int? currentPercent = WindowState == WindowState.Normal && WholeAppOverlayScreenFor(windowBox) is { } sizeScreen
            ? OverlayWindowSizing.CurrentPercent(windowBox, sourceWidth, sourceHeight, ToPixelBox(sizeScreen.WorkingArea),
                RenderScaling > 0 ? RenderScaling : 1.0, _wholeAppOverlayChosenPercent)
            : null;
        foreach (var percent in OverlayWindowSizing.SizePercents)
        {
            // The size the window is at now is ticked (round 3 #25); a border-resized window ticks nothing.
            var choice = MenuActions.Context(new MenuItem { Header = percent == currentPercent ? $"{percent}%  ✓" : $"{percent}%" }, "overlay.windowSizePercent");
            choice.Click += (_, _) => SetWholeAppOverlaySize(percent);
            sizeMenu.Items.Add(choice);
        }
        items.Add(sizeMenu);
        items.Add(new MenuItem { Header = "-" });

        var always = MenuActions.Tag(new MenuItem
        {
            Header = _settings.AlwaysStartWholeAppOverlayMode
                ? "Always enter Glide in Overlay mode  ✓"
                : "Always enter Glide in Overlay mode",
            Icon = MenuIcon("Overlays")
        }, "setting.overlay.wholeAppAlwaysStart");
        always.Click += (_, _) => ToggleAlwaysStartWholeAppOverlayMode();
        items.Add(always);

        var topmost = MenuActions.Tag(new MenuItem { Header = OverlayOptionsView.AlwaysOnTop ? "Always on top  ✓" : "Always on top", Icon = MenuIcon("AlwaysOnTop") }, "window.alwaysTop");
        topmost.Click += (_, _) => ToggleAlwaysOnTopSetting();
        items.Add(topmost);

        if (!includeExit) return;
        items.Add(new MenuItem { Header = "-" });
        items.Add(CreateExitWholeAppOverlayMenuItem());
    }

    /// <summary>"Exit Overlay mode" with its registry shortcut (overlay.toggleMode). The viewer menu shows it top-level under "Exit Glide".</summary>
    private MenuItem CreateExitWholeAppOverlayMenuItem()
    {
        var exit = MenuActions.Tag(new MenuItem { Header = MenuActions.Header("Exit Overlay mode", ShortcutForAction("overlay.toggleMode")), Icon = MenuIcon("Close") }, "overlay.toggleMode");
        exit.Click += (_, _) => ExitWholeAppOverlayMode();
        return exit;
    }

    private void SetWholeAppOverlaySize(int percent)
    {
        if (!_wholeAppOverlayMode || WindowState != WindowState.Normal) return;
        var current = CurrentWindowPixelBox();
        var screen = WholeAppOverlayScreenFor(current);
        if (screen is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : (screen.Scaling > 0 ? screen.Scaling : 1.0);
        var (sourceWidth, sourceHeight) = WholeAppOverlaySourcePixels();
        // Sizes above 100 % are capped to the work area and the window is moved fully inside it, so the
        // bottom button bar never ends up behind the taskbar (round 3 #26).
        var box = OverlayWindowSizing.ForPercent(percent, sourceWidth, sourceHeight, ToPixelBox(screen.WorkingArea), scale, current);
        _wholeAppOverlayChosenPercent = percent;
        ApplyWholeAppOverlayPixelBox(box, scale);
        SaveCurrentWholeAppOverlayPlacement(force: true);
    }

    private int? _wholeAppOverlayChosenPercent;

    private (int Width, int Height) WholeAppOverlaySourcePixels()
    {
        var pixels = Viewport.Bitmap?.PixelSize;
        return (Math.Max(1, _currentPixelWidth > 0 ? _currentPixelWidth : pixels?.Width ?? 1),
                Math.Max(1, _currentPixelHeight > 0 ? _currentPixelHeight : pixels?.Height ?? 1));
    }

    private static PixelBox ToPixelBox(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private PixelBox CurrentWindowPixelBox()
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var width = double.IsNaN(Width) ? Bounds.Width : Width;
        var height = double.IsNaN(Height) ? Bounds.Height : Height;
        return new PixelBox(Position.X, Position.Y, (int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>The screen the window overlaps most (its centre's screen when it overlaps none).</summary>
    private Screen? WholeAppOverlayScreenFor(PixelBox window)
    {
        Screen? best = null;
        long bestArea = 0;
        foreach (var screen in Screens.All)
        {
            var a = screen.WorkingArea;
            long w = Math.Max(0, Math.Min(window.Right, a.Right) - Math.Max(window.X, a.X));
            long h = Math.Max(0, Math.Min(window.Bottom, a.Bottom) - Math.Max(window.Y, a.Y));
            if (w * h > bestArea) { bestArea = w * h; best = screen; }
        }
        return best ?? Screens.ScreenFromPoint(new PixelPoint(window.X + window.Width / 2, window.Y + window.Height / 2)) ?? Screens.Primary;
    }

    private void ApplyWholeAppOverlayPixelBox(PixelBox box, double scale)
    {
        Position = new PixelPoint(box.X, box.Y);
        Width = box.Width / scale;
        Height = box.Height / scale;
    }

    /// <summary>Moves (and if needed shrinks) the Overlay window so it lies fully inside its monitor's work area.</summary>
    private void KeepWholeAppOverlayInsideWorkArea()
    {
        if (!_wholeAppOverlayMode || WindowState != WindowState.Normal) return;
        var current = CurrentWindowPixelBox();
        if (WholeAppOverlayScreenFor(current) is not { } screen) return;
        var inside = OverlayWindowSizing.KeepInside(current, ToPixelBox(screen.WorkingArea));
        if (inside == current) return;
        ApplyWholeAppOverlayPixelBox(inside, RenderScaling > 0 ? RenderScaling : 1.0);
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
