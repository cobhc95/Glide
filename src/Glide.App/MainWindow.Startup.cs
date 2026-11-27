using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Platform;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Imaging;

namespace Glide.App;

/// <summary>Startup, placement and headless diagnostic runners. Split from MainWindow (4.5.2); behavior unchanged.</summary>
public partial class MainWindow
{
    private async Task CompleteOpenedStartupAsync(IReadOnlyList<string> explicitStartupPaths)
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        if (!_deferWorkspaceActivation)
        {
            if (_earlyStartupTask is not null)
            {
                GlidePerformanceTrace.Mark("opened_early_startup_await_start");
                await _earlyStartupTask;
                GlidePerformanceTrace.Mark("opened_early_startup_await_end");
            }
            else
            {
                // Do not warm/read the startup file in parallel with the authoritative decoder. On a true
                // cold open that duplicated I/O can contend with WIC/Skia and pollute the file cache. The
                // foreground decoder is now the single owner of startup image reads.
                GlidePerformanceTrace.Mark("activate_workspace_start");
                await ActivateWorkspaceAsync();
                GlidePerformanceTrace.Mark("activate_workspace_end");
            }
        }
        if (_windowLifetimeCts.IsCancellationRequested) return;

        // Explicit launch history is bookkeeping, never a prerequisite for first useful pixels.
        if (_settings.RecentHistoryEnabled)
        {
            foreach (var path in explicitStartupPaths)
            {
                if (Directory.Exists(path)) RecentHistoryStore.RecordFolder(path, _settings.HistorySize);
                else if (File.Exists(path) && ImageNavigator.IsSupported(path)) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
            }
        }
        BeginDeferredRecentHistoryInitialization();
        ScheduleSvgWarmUp();
        ScheduleLegacyVideoDecoderCleanup();
        if (_pendingExternalReceiver)
            Dispatcher.UIThread.Post(() => _ = EnsurePendingExternalReceiverFallbackAsync(), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() =>
            MainRoot.AddHandler(InputElement.PointerReleasedEvent, WholeAppOverlayContextPointerReleased, RoutingStrategies.Bubble, false),
            DispatcherPriority.Background);
        if (!_diagnostics.Enabled) Dispatcher.UIThread.Post(_diagnostics.Enable, DispatcherPriority.Background);
        if (_settings.StartupDiagnostics) WriteStartupDiagnostics("window_opened");

        if (App.AutoExportDiagnostics || string.Equals(Environment.GetEnvironmentVariable("GLIDE_AUTO_EXPORT_DIAGNOSTICS"), "1", StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var target = App.AutoExportDiagnosticsFolder ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR");
                    await RunFullDiagnosticExportAsync(target);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Auto-diagnostic export failed: {ex}");
                }
                finally
                {
                    Close();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private WindowInWindowOverlayManager EnsureOverlays()
    {
        if (_overlays is { } existing) return existing;
        _overlayLoader ??= new ImageLoadCoordinator(_decoderBackend);
        var overlays = new WindowInWindowOverlayManager(OverlayCanvas, ImageView, _overlayLoader, this)
        {
            ImageRectForSize = size => Viewport.GetPresentationRectForViewportSize(size),
            IsFullscreen = () => WindowState == WindowState.FullScreen,
            FullscreenNavigationRequested = direction => _ = NavigateMouseAsync(direction)
        };
        overlays.Diagnostic += (name, data) => _diagnostics.Write("overlay", name, data);
        overlays.OverlayCountChanged += UpdateOverlayStatusButtons;
        overlays.OverlayCountChanged += QueueWholeAppOverlayRegionUpdate;   // Overlay mode: picture-in-picture frames are part of its input region
        overlays.LayoutChanged += QueueWholeAppOverlayRegionUpdate;
        overlays.ContextMenuVisibilityChanged += open =>
        {
            if (open) _overlayContextTopmostScope ??= EnterTopmostTransient();
            else { _overlayContextTopmostScope?.Dispose(); _overlayContextTopmostScope = null; }
        };
        overlays.OpenFileLocationRequested = path => ExplorerShell.Reveal(path, directory: false);
        // Read the held keys now, while the menu item is being clicked (Shift / Alt flip the new-tab settings).
        overlays.OpenInNewTabLabel = OpenInNewTabMenuLabel;
        overlays.OpenInNewTabRequested = path => { var held = ModifierKeyState.Current(); Dispatcher.UIThread.Post(async () => await OpenImageInNewTabAsync(path, held, "overlay_menu")); };
        overlays.OpenWithRequested = path => WindowsOpenWith.Show(path);
        overlays.VideoUnavailable += (path, reason) => Dispatcher.UIThread.Post(async () => await ShowVideoUnavailableAsync(path, reason));
        ApplyOverlaySettingsToManager(overlays);
        _overlays = overlays;
        return overlays;
    }

    private void ApplyOverlaySettingsToManager(WindowInWindowOverlayManager overlays)
    {
        overlays.DefaultOpacity = Math.Clamp(OverlayOptionsView.DefaultOpacity / 100.0, .1, 1.0);
        // 4.8.0: overlay wheel zoom, Ctrl+wheel zoom and right-drag pan are Window in Window gesture
        // rows ("Do nothing" = the old toggle off); the built-in defaults are on.
        overlays.WheelZoomEnabled = true;
        overlays.CtrlWheelZoomEnabled = true;
        overlays.RightDragPanEnabled = !string.Equals(_inputRouter.ResolveGesture("overlay.rightDrag", _settings.Gestures), GestureCatalog.Nothing, StringComparison.OrdinalIgnoreCase);
        overlays.HighlightSelected = OverlayOptionsView.SelectedHighlight;
        overlays.RememberZoom = OverlayOptionsView.RememberZoom;
        overlays.ZoomStepPercent = Math.Clamp(OverlayOptionsView.ZoomStepPercent, 1, 100);
        overlays.AdaptivePositioningEnabled = !OverlayOptionsView.AbsoluteCoordinatesOnResize;
        overlays.ScaleWithHostResize = OverlayOptionsView.ScaleWithWindow;
        overlays.AnimationRegion = OverlayOptionsView.AnimationRegion ?? "Anywhere";
        overlays.AnimationSpeedDipsPerSecond = Math.Clamp(OverlayOptionsView.AnimationSpeedDipsPerSecond, 10, 2000);
        overlays.AnimationTurnIntervalMs = Math.Clamp(OverlayOptionsView.AnimationTurnIntervalMs, 0, 60000);
        overlays.AnimationTurnAngleDegrees = Math.Clamp(OverlayOptionsView.AnimationTurnAngleDegrees, 0, 180);
        overlays.AnimationPauseWhileInteracting = OverlayOptionsView.AnimationPauseWhileInteracting;
        overlays.AnimationAvoidOverlap = OverlayOptionsView.AnimationAvoidOverlap;
        overlays.AnimationRefreshRateHz = Math.Clamp(OverlayOptionsView.AnimationRefreshRateHz, 60, 360);
        overlays.AccentBrush = new SolidColorBrush(Color.Parse(CurrentAccentHex()));
        overlays.ResolveGesture = (slot, modifiers) => ResolveGestureSlot(slot, modifiers);
        overlays.RefreshTheme();
    }

    private SlideshowSessionController EnsureSlideshow()
    {
        if (_slideshow is { } existing) return existing;
        var slideshow = new SlideshowSessionController(
            new DispatcherTimer(), () => ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath),
            () => _navigator.Count, () => _navigator.Index, () => SlideshowOptionsView.CrossFolders,
            () => SlideshowOptionsView.Loop, () => SlideshowOptionsView.Shuffle,
            () => string.Equals(SlideshowOptionsView.Direction, "Backward", StringComparison.OrdinalIgnoreCase) ? -1 : 1,
            () => SlideshowOptionsView.StartFullscreen, () => SlideshowOptionsView.IntervalMs,
            ConfirmSlideshowSessionOptionsAsync, Viewport.CaptureViewState, Viewport.RestoreViewState,
            () => WindowState == WindowState.FullScreen, ToggleFullscreen,
            direction => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(direction, forceDirectionalEdge: true)),
            direction => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(direction);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                if (direction < 0) _navigator.Last(); else _navigator.First();
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            direction => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(direction);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                _navigator.Move(direction);
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            index => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(index - _navigator.Index);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                _navigator.SelectIndex(index);
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            // Slideshow should consume the prepared neighbour frame immediately. Re-check session
            // state *after* acquiring the navigation gate: Home/manual input may have stopped the
            // slideshow while this continuation was queued behind another navigation operation.
            () => RunSerializedNavigationAsync(async () =>
            {
                if (_slideshow?.IsRunning != true || !ImageView.IsVisible) return;
                await PresentCurrentAsync(forceFull: false).ConfigureAwait(true);
            }), UpdateSlideshowUi,
            (name, data) => _diagnostics.Write("slideshow", name, data));
        _slideshow = slideshow;
        return slideshow;
    }

    private readonly bool _isTransferredTabWindow;

    internal MainWindow(TabState transferredTab, GlideSettingsState inheritedSettings, bool deferWorkspaceActivation = false) : this()
    {
        _settings = inheritedSettings.CloneState();
        _startupSettingsBaseline = _settings.CloneState();
        _deferredStartupSettingsAdopted = true;
        _deferWorkspaceActivation = deferWorkspaceActivation;
        // A torn-off/detached window holds exactly the transferred tab: it must never run the
        // startup action ("Open last session" used to copy every tab into it).
        _isTransferredTabWindow = true;
        // The base constructor remembered the live saved placement for RestoreValidatedStartupNormalPosition, which re-applies it
        // once the HWND is up. That is for the first window of a launch only: a detached / duplicated window is placed by its
        // creator (drop point, cascade) and must not jump to the saved main-window box a moment after it is shown.
        _validatedStartupNormalPosition = null;
        _workspace.ResetForDetached(transferredTab, _settings.DetachedWindowHomeTab);
    }

    public void QueueOpen(string path)
    {
        _hasExplicitStartupOpen = true;
        _queuedOpen = path;
        _diagnostics.Write("launch", "queue_open", new { path });
    }

    internal void PrepareForPendingExternalOpen()
    {
        // A Speed Boost standby receiver is not a standalone launch. The broker will populate its
        // workspace immediately after Opened; suppressing the no-file startup destination avoids a
        // transient or persistent Home tab beside the requested external image.
        _pendingExternalReceiver = true;
        _hasExplicitStartupOpen = true;
        _diagnostics.Write("launch", "pending_external_receiver_prepared");
    }

    private async Task EnsurePendingExternalReceiverFallbackAsync()
    {
        // Give the broker's already-accepted request a chance to reach the receiver first. If no
        // request arrives (for example, a warm activation raced the final standby close), never
        // leave the newly-created window with zero tabs or an Alt-Tab-only ghost shell.
        try { await Task.Delay(250, _windowLifetimeCts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (!_pendingExternalReceiver || _windowLifetimeCts.IsCancellationRequested || _workspace.Tabs.Count > 0)
        {
            _pendingExternalReceiver = false;
            return;
        }

        _pendingExternalReceiver = false;
        ApplyConfiguredStartupWorkspace();
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        _diagnostics.Write("launch", "pending_external_receiver_fallback_home");
    }

    public void QueueOpenPaths(IEnumerable<string> paths)
    {
        var materialized = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (materialized.Length > 0) _hasExplicitStartupOpen = true;
        _startupPaths.Add(materialized,
            path => Directory.Exists(path) || (File.Exists(path) && ImageNavigator.IsSupported(path)),
            (path, reason) => _diagnostics.Write("launch", reason.StartsWith("invalid", StringComparison.OrdinalIgnoreCase) ? "invalid_path_skipped" : "unsupported_path_skipped", new { path, reason }));
    }

    public void TryPrepareEarlyStartupPresentation()
    {
        if (_deferWorkspaceActivation) return;
        var explicitStartupPaths = ApplyQueuedStartupDestinations();
        if (explicitStartupPaths.Length == 0 || _workspace.Active is not ImageTabState imageTab) return;

        GlidePerformanceTrace.Mark("early_startup_prep_start", Path.GetFileName(imageTab.Path));
        _hasExplicitStartupOpen = true;
        _earlyStartupPaths = explicitStartupPaths;
        AdvanceWorkspaceEpoch();
        _earlyStartupTask = LoadPathAsync(imageTab.Path, updateActiveTab: false);
        GlidePerformanceTrace.Mark("early_startup_prep_end");
    }

    private string[] ApplyQueuedStartupDestinations()
    {
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(_queuedOpen)) raw.Add(_queuedOpen);
        _queuedOpen = null;
        raw.AddRange(_startupPaths.Drain());

        var plan = StartupWorkspacePlanner.Create(raw);
        if (plan.Tabs.Count == 0) return Array.Empty<string>();
        _workspace.Reset(plan.Tabs, 0);
        _diagnostics.Write("launch", "explicit_startup_workspace", new { count = plan.Tabs.Count, active = plan.Paths[0] });
        return plan.Paths.ToArray();
    }

    private void AdoptStartupSettingsIfReady(bool applyVisuals)
    {
        if (_deferredStartupSettingsAdopted || _windowLifetimeCts.IsCancellationRequested) return;
        var loaded = App.TryGetStartupSettings();
        if (loaded is null) return;
        _settings = SettingsStore.MergeHydratedPreservingEdits(_startupSettingsBaseline, _settings, loaded);
        _startupSettingsBaseline = _settings.CloneState();
        _deferredStartupSettingsAdopted = true;
        App.PublishCurrentSettings(_settings);
        if (applyVisuals)
        {
            ApplySettingsVisuals();
            ApplyStartupWholeAppOverlayPreference();
            TryScheduleAutomaticOverlayLayoutRestore();
        }
        _diagnostics.Write("startup", "settings_adopted", new { deferred = true });
    }

    private void ContinueDeferredStartupSettingsAdoption()
    {
        if (_deferredStartupSettingsAdopted) return;
        var task = App.StartupSettingsTask;
        if (task is null) return;
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_windowLifetimeCts.IsCancellationRequested) return;
            AdoptStartupSettingsIfReady(applyVisuals: true);
        }, DispatcherPriority.Background), TaskScheduler.Default);
    }

    private static int _svgWarmUpScheduled;

    /// <summary>
    /// 5.2.0: once per process, when the UI has gone idle (application-idle priority) and a couple of seconds after the first
    /// picture, Svg.Skia and the Direct2D SVG route are initialised on a below-normal-priority thread, so the first SVG tile
    /// or the first SVG opened does not pay the ~100 ms start-up.
    /// </summary>
    private void ScheduleSvgWarmUp()
    {
        if (Interlocked.Exchange(ref _svgWarmUpScheduled, 1) != 0) return;
        var token = _windowLifetimeCts.Token;
        Dispatcher.UIThread.Post(() => _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                var thread = new Thread(SvgTileRenderer.WarmUp) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Glide SVG warm-up" };
                thread.Start();
            }
            catch (OperationCanceledException) { /* the window closed first */ }
        }), DispatcherPriority.ApplicationIdle);
    }

    private static int _legacyVideoDecoderCleanupScheduled;

    /// <summary>
    /// Once per process, well after startup: deletes the video decoder files Glide 4.6 to 5.0.3 could download into
    /// %LOCALAPPDATA%\Glide. Glide no longer uses them (videos play only with the codecs installed in Windows).
    /// </summary>
    private void ScheduleLegacyVideoDecoderCleanup()
    {
        if (Interlocked.Exchange(ref _legacyVideoDecoderCleanupScheduled, 1) != 0) return;
        var token = _windowLifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide");
                var removed = Glide.Core.Reset.LegacyVideoDecoderCleanup.RemoveLeftovers(local);
                if (removed > 0) _diagnostics.Write("startup", "legacy_video_decoder_removed", new { folders = removed });
            }
            catch (OperationCanceledException) { /* window closed first: the next start tries again */ }
        }, CancellationToken.None);
    }

    private void BeginDeferredRecentHistoryInitialization()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_initialization_deferred");
        Dispatcher.UIThread.Post(() =>
        {
            _ = RecentHistoryStore.InitializeAsync().ContinueWith(
                _ => Dispatcher.UIThread.Post(RefreshRecentHistoryHome),
                TaskScheduler.Default);
        }, DispatcherPriority.Background);
    }

    private void ApplyInitialReferenceSizeAndPlacement()
    {
        if (_startupPlacementApplied) return;
        _startupPlacementApplied = true;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;

        Width = 1596.0 / scale;
        Height = 1031.0 / scale;
        MinWidth = 180.0 / scale;
        MinHeight = 520.0 / scale;

        if (!_settings.RememberWindowPlacement || _settings.WindowWidth < 400 || _settings.WindowHeight < 300)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        var saved = new PixelPoint(_settings.WindowX, _settings.WindowY);
        if (IsSafeSavedWindowPosition(saved, Width, Height, scale))
        {
            _validatedStartupNormalPosition = saved;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = saved;
        }
        else
        {
            CenterOnCurrentPrimaryScreen(scale);
        }
        _lastNormalWindowPosition = Position;
        _lastNormalWindowSize = new Size(Width, Height);
        _normalPlacementKnown = true;
        if (_settings.WindowWasMaximized)
        {
            WindowState = WindowState.Maximized;
            _lastKnownWindowState = WindowState.Maximized;
            _stateBeforeMinimize = WindowState.Maximized;
        }
        else
        {
            _lastKnownWindowState = WindowState.Normal;
            _stateBeforeMinimize = WindowState.Normal;
        }
    }

    private void RestoreValidatedStartupNormalPosition()
    {
        // Avalonia can apply a pre-show Position before the Windows HWND has its final frame
        // metrics. Reapply only the validated normal placement after the HWND is visible; native
        // maximized/fullscreen/overlay states deliberately use their own restoration paths.
        if (!OperatingSystem.IsWindows() || !IsVisible || _speedBoostStandbyActive ||
            _wholeAppOverlayMode || WindowState != WindowState.Normal ||
            _validatedStartupNormalPosition is not { } saved) return;
        Position = saved;
        _lastNormalWindowPosition = saved;
        _normalPlacementKnown = true;
    }

    private bool IsSafeSavedWindowPosition(PixelPoint position, double widthDip, double heightDip, double scale)
    {
        if (position.X == int.MinValue || position.Y == int.MinValue || Math.Abs(position.X) >= 50000 || Math.Abs(position.Y) >= 50000) return false;
        var w = Math.Max(240, (int)Math.Round(widthDip * scale));
        var h = Math.Max(160, (int)Math.Round(heightDip * scale));
        foreach (var screen in Screens.All)
        {
            var area = screen.WorkingArea;
            var overlapW = Math.Max(0, Math.Min(position.X + w, area.Right) - Math.Max(position.X, area.X));
            var overlapH = Math.Max(0, Math.Min(position.Y + h, area.Bottom) - Math.Max(position.Y, area.Y));
            if (overlapW >= 80 && overlapH >= 80) return true;
        }
        return false;
    }

    private void CenterOnCurrentPrimaryScreen(double scale)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var widthPx = Math.Min(area.Width, Math.Max(1, (int)Math.Round(Width * scale)));
        var heightPx = Math.Min(area.Height, Math.Max(1, (int)Math.Round(Height * scale)));
        Position = new PixelPoint(area.X + (area.Width - widthPx) / 2, area.Y + (area.Height - heightPx) / 2);
    }

    /// <summary>How long after the last size change the transient pointer state is cleared once more.</summary>
    internal const int ResizeInputResetDelayMs = 90;

    /// <summary>
    /// Clears transient pointer ownership once the size has stopped changing for <see cref="ResizeInputResetDelayMs"/>.
    /// SizeChanged fires for every WM_SIZE of a drag (hundreds per second), and this used to allocate a CancellationTokenSource,
    /// a thread-pool task and a delay timer for each one. Now one dispatcher timer is armed per burst: every size change only
    /// moves the deadline, and the timer re-arms itself for the remainder until the burst is over.
    /// </summary>
    private void ScheduleResizeInputReset()
    {
        _resizeInputResetDeadlineTicks = Environment.TickCount64 + ResizeInputResetDelayMs;
        if (_resizeInputResetArmed) return;
        _resizeInputResetArmed = true;
        ArmResizeInputResetTimer(ResizeInputResetDelayMs);
    }

    private void ArmResizeInputResetTimer(long delayMs)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (_windowLifetimeCts.IsCancellationRequested) { _resizeInputResetArmed = false; return; }
            var remaining = _resizeInputResetDeadlineTicks - Environment.TickCount64;
            if (remaining > 0) { ArmResizeInputResetTimer(remaining); return; }
            _resizeInputResetArmed = false;
            EndManualWindowDrag();
            Viewport.CancelInteraction();
        }, TimeSpan.FromMilliseconds(Math.Max(1, delayMs)), DispatcherPriority.Input);
    }

    /// <summary>The geometry last handed to <see cref="SettingsService.Publish"/> by placement tracking: skips republishing the same box.</summary>
    private (int X, int Y, double W, double H, bool Maximized)? _publishedPlacement;

    private void TrackNormalWindowPlacement()
    {
        // Overlay mode is intentionally transient presentation geometry. Never let moving/resizing
        // the frameless overlay overwrite Glide's remembered ordinary-window placement.
        if (_wholeAppOverlayMode || _applyingWholeAppOverlayPlacement || !_startupPlacementApplied || WindowState != WindowState.Normal || Bounds.Width < 100 || Bounds.Height < 100) return;
        // Windows flips the native maximized/minimized state before Avalonia's cached WindowState catches
        // up, so a resize fired mid-maximize still reads "Normal" but carries the maximized geometry.
        // Trust the native state too, or the maximized rectangle overwrites the real normal bounds.
        if (TryGetNativeZoomed(out var nativeZoomed) && nativeZoomed) return;
        // Same for minimize: Windows parks an iconic window at (-32000, -32000) and can raise a
        // position change before Avalonia's WindowState reads Minimized. Saving that parking spot
        // replaced the user's real placement (the next launch then fell back to centering).
        if (TryGetNativeIconic(out var nativeIconic) && nativeIconic) return;
        if (IsMinimizedParkingPosition(Position)) return;
        _lastNormalWindowPosition = Position;
        _lastNormalWindowSize = Bounds.Size;
        _normalPlacementKnown = true;
        _settings.WindowX = _lastNormalWindowPosition.X;
        _settings.WindowY = _lastNormalWindowPosition.Y;
        _settings.WindowWidth = _lastNormalWindowSize.Width;
        _settings.WindowHeight = _lastNormalWindowSize.Height;
        _settings.WindowWasMaximized = false;
        // Publishing clones the whole settings state and runs every Changed subscriber, which is far too heavy to repeat for each
        // WM_SIZE / WM_WINDOWPOSCHANGED of a drag. While Windows' modal size/move loop runs the box is only remembered here;
        // ResetPointerStateAfterNativeResize (WM_EXITSIZEMOVE) publishes the final one. Outside the loop (snap, programmatic
        // resize, restore) an unchanged box is not published again.
        if (_nativeSizeMoveActive) return;
        var box = (_lastNormalWindowPosition.X, _lastNormalWindowPosition.Y, _lastNormalWindowSize.Width, _lastNormalWindowSize.Height, false);
        if (_publishedPlacement == box) return;
        _publishedPlacement = box;
        App.PublishCurrentSettings(_settings);
    }

    /// <summary>Windows' minimized-window parking coordinates (-32000, -32000); never a real placement.</summary>
    internal static bool IsMinimizedParkingPosition(PixelPoint position) => position.X <= -32000 || position.Y <= -32000;

    private void SaveWindowPlacement()
    {
        if (!_settings.RememberWindowPlacement || WindowState == WindowState.FullScreen) return;

        if (_wholeAppOverlayMode && _wholeAppOverlaySnapshot is { } overlaySnapshot)
        {
            _settings.WindowWasMaximized = overlaySnapshot.RestoreState == WindowState.Maximized;
            _settings.WindowX = overlaySnapshot.Position.X;
            _settings.WindowY = overlaySnapshot.Position.Y;
            _settings.WindowWidth = overlaySnapshot.Width;
            _settings.WindowHeight = overlaySnapshot.Height;
            SaveSettingsAndPublish();
            return;
        }

        _settings.WindowWasMaximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _stateBeforeMinimize == WindowState.Maximized);
        if (WindowState == WindowState.Normal) TrackNormalWindowPlacement();
        if (_normalPlacementKnown)
        {
            _settings.WindowX = _lastNormalWindowPosition.X;
            _settings.WindowY = _lastNormalWindowPosition.Y;
            _settings.WindowWidth = _lastNormalWindowSize.Width;
            _settings.WindowHeight = _lastNormalWindowSize.Height;
        }
        SaveSettingsAndPublish();
    }

    internal async Task RunLiveDiagnosticNavigationAsync(string fixtureFolder, string reportFolder)
    {
        var files = Directory.Exists(fixtureFolder)
            ? Directory.EnumerateFiles(fixtureFolder, "browse_*.jpg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        if (files.Length == 0)
        {
            // A skipped/non-Windows corpus is expected during development. Keep the export
            // machine-readable instead of silently omitting evidence (which could be mistaken for
            // an empty or zero-latency run).
            Directory.CreateDirectory(reportFolder);
            File.WriteAllLines(Path.Combine(reportFolder, "live_navigation_results.csv"), new[]
            {
                "status,result",
                "SKIP,No generated navigation corpus was available; generate it on Windows before timing navigation."
            });
            File.WriteAllText(Path.Combine(reportFolder, "live_navigation_status.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = "SKIP",
                ["corpus"] = Path.GetFileName(fixtureFolder),
                ["fileCount"] = 0,
                ["reason"] = "Performance corpus was absent or platform/dependency generation was skipped."
            }.ToJsonString(AppJsonContext.Indented));
            return;
        }
        var restorePath = _currentPath;
        var rows = new List<string> { "index,file,present_ms" };
        var completed = false;
        try
        {
            await OpenAsImageTabAsync(files[0]);
            await Task.Delay(40);
            for (var i = 0; i < files.Length; i++)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                if (i > 0) await NavigateAsync(1);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                rows.Add($"{i + 1},{Path.GetFileName(files[i])},{elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                // Yield to Avalonia so this is a real visible presentation exercise, not a headless
                // decoder loop. Representative frames are captured from the production MainWindow.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
                if (i == 0 || (i + 1) % 30 == 0 || i == files.Length - 1)
                    Glide.App.Diagnostics.UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, $"live_navigation_{i + 1:000}.png"), out _);
            }
            completed = true;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(reportFolder, "live_navigation_results.csv"), rows);
            File.WriteAllText(Path.Combine(reportFolder, "live_navigation_status.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = completed ? "PASS" : "FAILED",
                ["corpus"] = Path.GetFileName(fixtureFolder),
                ["fileCount"] = files.Length,
                ["result"] = "live_navigation_results.csv"
            }.ToJsonString(AppJsonContext.Indented));
            var fixtureRoot = Directory.GetParent(fixtureFolder)?.FullName;
            if (!string.IsNullOrWhiteSpace(fixtureRoot))
            {
                File.WriteAllText(Path.Combine(fixtureRoot, "performance_corpus_status.json"), new System.Text.Json.Nodes.JsonObject
                {
                    ["corpus"] = Path.GetFileName(fixtureFolder),
                    ["generated"] = true,
                    ["status"] = completed ? "LIVE_PASS" : "LIVE_FAILED",
                    ["fileCount"] = files.Length,
                    ["evidence"] = Path.Combine(reportFolder, "live_navigation_results.csv")
                }.ToJsonString(AppJsonContext.Indented));
            }
            if (!string.IsNullOrWhiteSpace(restorePath) && File.Exists(restorePath))
                await OpenAsImageTabAsync(restorePath);
        }
    }


    internal async Task RunLargeImageFirstPaintDiagnosticAsync(string fixturePath, string reportFolder)
    {
        Directory.CreateDirectory(reportFolder);
        var output = Path.Combine(reportFolder, "large_image_first_paint.csv");
        if (!File.Exists(fixturePath))
        {
            File.WriteAllLines(output, new[]
            {
                "status,fixture,present_ms,decode_ms,route,preview_width,preview_height,progressive_colour_first",
                $"SKIP,{Path.GetFileName(fixturePath)},0,0,missing,0,0,{_settings.ProgressiveColorFirstPreview}"
            });
            return;
        }

        var restorePath = _currentPath;
        var rows = new List<string>
        {
            "status,fixture,present_ms,decode_ms,route,preview_width,preview_height,progressive_colour_first"
        };
        try
        {
            // This is deliberately an application-cache-purged in-process measurement, not a claim
            // of process/OS-cold launch. The standalone benchmark remains authoritative for true cold
            // startup. Its purpose is to make the 21 MP regression class and decode route visible in
            // every Developer Options export.
            _loader.PurgeCaches();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await OpenAsImageTabAsync(fixturePath);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
            var presentMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            rows.Add(string.Join(',',
                "PASS",
                Path.GetFileName(fixturePath),
                presentMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                _currentFirstFrameDecodeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                _currentDecodeRoute,
                _currentFirstFrameWidth,
                _currentFirstFrameHeight,
                _settings.ProgressiveColorFirstPreview));
            Glide.App.Diagnostics.UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, "large_image_first_paint.png"), out _);
        }
        catch (Exception ex)
        {
            rows.Add($"FAIL,{Path.GetFileName(fixturePath)},0,0,{ex.GetType().Name},0,0,{_settings.ProgressiveColorFirstPreview}");
        }
        finally
        {
            File.WriteAllLines(output, rows);
            if (!string.IsNullOrWhiteSpace(restorePath) && File.Exists(restorePath) &&
                !string.Equals(restorePath, fixturePath, StringComparison.OrdinalIgnoreCase))
                await OpenAsImageTabAsync(restorePath);
        }
    }
}
