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
        overlays.ContextMenuVisibilityChanged += open =>
        {
            if (open) _overlayContextTopmostScope ??= EnterTopmostTransient();
            else { _overlayContextTopmostScope?.Dispose(); _overlayContextTopmostScope = null; }
        };
        overlays.OpenFileLocationRequested = path => RevealInWindowsExplorer(path, directory: false);
        overlays.OpenInNewTabRequested = path => Dispatcher.UIThread.Post(async () => await OpenAsImageTabAsync(path, openInNewTab: true));
        overlays.OpenWithRequested = path => WindowsOpenWith.Show(path);
        VideoOverlaySession.DecoderPackRequested ??= RequestVideoDecoderPackAsync;
        overlays.VideoUnavailable += (path, reason) => Dispatcher.UIThread.Post(async () => await ShowVideoUnavailableAsync(path, reason));
        ApplyOverlaySettingsToManager(overlays);
        _overlays = overlays;
        return overlays;
    }

    private void ApplyOverlaySettingsToManager(WindowInWindowOverlayManager overlays)
    {
        overlays.DefaultOpacity = Math.Clamp(_settings.OverlayDefaultOpacity / 100.0, .1, 1.0);
        overlays.WheelZoomEnabled = _settings.OverlayWheelZoom;
        overlays.CtrlWheelZoomEnabled = _settings.CtrlWheelZoom;
        overlays.RightDragPanEnabled = _settings.OverlayRightDragPan;
        overlays.HighlightSelected = _settings.OverlaySelectedHighlight;
        overlays.RememberZoom = _settings.OverlayRememberZoom;
        overlays.ZoomStepPercent = Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100);
        overlays.AdaptivePositioningEnabled = !_settings.OverlayAbsoluteCoordinatesOnResize;
        overlays.ScaleWithHostResize = _settings.OverlayScaleWithWindow;
        overlays.AnimationRegion = _settings.OverlayAnimationRegion ?? "Anywhere";
        overlays.AnimationSpeedDipsPerSecond = Math.Clamp(_settings.OverlayAnimationSpeedDipsPerSecond, 10, 2000);
        overlays.AnimationTurnIntervalMs = Math.Clamp(_settings.OverlayAnimationTurnIntervalMs, 0, 60000);
        overlays.AnimationTurnAngleDegrees = Math.Clamp(_settings.OverlayAnimationTurnAngleDegrees, 0, 180);
        overlays.AnimationPauseWhileInteracting = _settings.OverlayAnimationPauseWhileInteracting;
        overlays.AnimationAvoidOverlap = _settings.OverlayAnimationAvoidOverlap;
        overlays.AnimationRefreshRateHz = Math.Clamp(_settings.OverlayAnimationRefreshRateHz, 60, 360);
        overlays.AccentBrush = new SolidColorBrush(Color.Parse(CurrentAccentHex()));
        overlays.ResolveGesture = slot => _inputRouter.ResolveGesture(slot, _settings.Gestures);
        overlays.RefreshTheme();
    }

    private SlideshowSessionController EnsureSlideshow()
    {
        if (_slideshow is { } existing) return existing;
        var slideshow = new SlideshowSessionController(
            new DispatcherTimer(), () => ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath),
            () => _navigator.Count, () => _navigator.Index, () => _settings.SlideshowCrossFolders,
            () => _settings.SlideshowLoop, () => _settings.SlideshowShuffle,
            () => string.Equals(_settings.SlideshowDirection, "Backward", StringComparison.OrdinalIgnoreCase) ? -1 : 1,
            () => _settings.SlideshowStartFullscreen, () => _settings.SlideshowIntervalMs,
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

    internal MainWindow(TabState transferredTab, GlideSettingsState inheritedSettings, bool deferWorkspaceActivation = false) : this()
    {
        _settings = inheritedSettings.CloneState();
        _startupSettingsBaseline = _settings.CloneState();
        _deferredStartupSettingsAdopted = true;
        _deferWorkspaceActivation = deferWorkspaceActivation;
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

    private void TrackNormalWindowPlacement()
    {
        // Overlay mode is intentionally transient presentation geometry. Never let moving/resizing
        // the frameless overlay overwrite Glide's remembered ordinary-window placement.
        if (_wholeAppOverlayMode || !_startupPlacementApplied || WindowState != WindowState.Normal || Bounds.Width < 100 || Bounds.Height < 100) return;
        _lastNormalWindowPosition = Position;
        _lastNormalWindowSize = Bounds.Size;
        _normalPlacementKnown = true;
        _settings.WindowX = _lastNormalWindowPosition.X;
        _settings.WindowY = _lastNormalWindowPosition.Y;
        _settings.WindowWidth = _lastNormalWindowSize.Width;
        _settings.WindowHeight = _lastNormalWindowSize.Height;
        _settings.WindowWasMaximized = false;
        App.PublishCurrentSettings(_settings);
    }

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
