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

/// <summary>Workspace loading, presentation, refinement, rate limiting and sibling-folder navigation. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private async Task OpenAsImageTabAsync(string path, bool openInNewTab = false)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path))
        {
            Title = AppVersion.UnsupportedImageTitle;
            return;
        }
        if (openInNewTab || !_workspace.ReplaceActiveWithImage(path)) _workspace.AddImage(path);
        RebuildTabStrip();
        await LoadPathAsync(path, updateActiveTab: true);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
    }

    private void CancelAsyncWorkspaceWork()
    {
        _loadCts?.Cancel();
        _metadataCts?.Cancel();
        _folderIndexCts?.Cancel();
        _rapidNavigationSettleCts?.Cancel();
        _heldNavigationCts?.Cancel();
        _pendingNavigationDelta = 0;
        lock (_wheelNavigationSync) _wheelNavigationQueue.Clear();
    }

    private void AdvanceWorkspaceEpoch()
    {
        Interlocked.Increment(ref _workspaceEpoch);
        CancelAsyncWorkspaceWork();
    }

    private ImageRequestContext BeginImageRequest(string path)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var tabId = _workspace.Active?.Id ?? Guid.Empty;
        return new ImageRequestContext(
            _windowLifetimeId, tabId, Volatile.Read(ref _workspaceEpoch),
            Interlocked.Increment(ref _imageRequestId), path, _loadCts.Token);
    }

    private bool IsRequestCurrent(in ImageRequestContext request, bool requirePresented = false)
    {
        if (_speedBoostStandbyActive || _windowLifetimeCts.IsCancellationRequested ||
            !request.Matches(_windowLifetimeId, _workspace.Active?.Id ?? Guid.Empty,
                Volatile.Read(ref _workspaceEpoch), Volatile.Read(ref _imageRequestId), _currentPath))
            return false;
        return !requirePresented || (ImageView.IsVisible &&
            string.Equals(_presentedPath, request.Path, StringComparison.OrdinalIgnoreCase) &&
            Viewport.PresentationRequestId == request.ImageRequestId);
    }

    private bool IsFolderSessionCurrent(in FolderIndexSession session)
    {
        if (_windowLifetimeCts.IsCancellationRequested) return false;
        return session.Matches(_workspace.Active?.Id ?? Guid.Empty, Volatile.Read(ref _workspaceEpoch),
            Volatile.Read(ref _folderSessionId), _currentPath);
    }

    private async Task LoadPathAsync(string path, bool updateActiveTab)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        _folderIndexCts?.Cancel();
        _folderIndexCts?.Dispose();
        _folderIndexCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var folderSession = new FolderIndexSession(
            _workspace.Active?.Id ?? Guid.Empty,
            Volatile.Read(ref _workspaceEpoch),
            Interlocked.Increment(ref _folderSessionId),
            Path.GetDirectoryName(path) ?? string.Empty,
            _folderIndexCts.Token);
        _pendingNavigationDelta = 0;
        _lastNavigationDirection = 0;

        _navigator.OpenSingleOnly(path);
        EnsureFolderWatch(Path.GetDirectoryName(Path.GetFullPath(path)));
        _folderIndexReady = false;
        _folderIndexCanonicalReady = false;

        if (updateActiveTab)
        {
            _workspace.ReplaceActiveImage(path);
            folderSession = folderSession with { ActiveTabId = _workspace.Active?.Id ?? Guid.Empty };
        }

        var presentation = await PresentCurrentAsync();
        if (presentation.Accepted && IsFolderSessionCurrent(folderSession))
            _ = IndexContainingFolderAsync(path, folderSession);
    }

    private async Task IndexContainingFolderAsync(string requestedPath, FolderIndexSession session)
    {
        try
        {
            if (!IsFolderSessionCurrent(session)) return;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_start", Path.GetFileName(requestedPath));
            var provisional = await Task.Run(
                () => ImageNavigator.EnumerateSupportedFolder(requestedPath, maxCount: 64),
                session.Token);
            if (!IsFolderSessionCurrent(session)) return;

            _navigator.ReplaceFolderIndex(provisional, requestedPath);
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexReady = true;
            _folderIndexCanonicalReady = false;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_first_neighbour_ready", $"count={_navigator.Count}");
            UpdatePictureCounter();
            UpdateStatusStats();
            _diagnostics.Write("navigation", "folder_index_provisional", new { path = requestedPath, count = _navigator.Count, bounded = true });
            ScheduleNavigationNeighbours();

            var pending = _pendingNavigationDelta;
            _pendingNavigationDelta = 0;
            if (pending != 0 && IsFolderSessionCurrent(session))
            {
                _lastNavigationDirection = Math.Sign(pending);
                var availableBackward = _navigator.Index;
                var availableForward = Math.Max(0, _navigator.Count - 1 - _navigator.Index);
                var safeDelta = Math.Clamp(pending, -availableBackward, availableForward);
                var remainder = pending - safeDelta;
                if (remainder != 0)
                    _pendingNavigationDelta = Math.Clamp(remainder, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands);
                if (safeDelta != 0)
                {
                    _navigator.Move(safeDelta);
                    await PresentCurrentAsync();
                    if (!IsFolderSessionCurrent(session)) return;
                }
            }

            var paths = await Task.Run(() => ImageNavigator.EnumerateSupportedFolder(requestedPath), session.Token);
            if (!IsFolderSessionCurrent(session)) return;
            var current = _navigator.Current ?? requestedPath;
            _navigator.ReplaceFolderIndex(paths, current);
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexCanonicalReady = true;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_complete", $"count={_navigator.Count}");
            UpdatePictureCounter();
            UpdateStatusStats();
            _diagnostics.Write("navigation", "folder_index_ready", new { path = requestedPath, count = _navigator.Count, reconciled = true });
            ScheduleNavigationNeighbours();

            var deferred = _pendingNavigationDelta;
            _pendingNavigationDelta = 0;
            if (deferred != 0 && IsFolderSessionCurrent(session))
            {
                _lastNavigationDirection = Math.Sign(deferred);
                _navigator.Move(deferred);
                await PresentCurrentAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexReady = true;
            _folderIndexCanonicalReady = true;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_failed", ex.GetType().Name);
            _diagnostics.Write("navigation", "folder_index_failed", new { path = requestedPath, error = ex.GetType().Name, ex.Message });
        }
    }

    private void EnsureFolderWatch(string? folder)
    {
        try
        {
            _folderWatch ??= new FolderWatchService((watched, set) =>
                Dispatcher.UIThread.Post(() => _ = ApplyFolderChangesAsync(watched, set), DispatcherPriority.Background));
            _folderWatch.Watch(folder);
        }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "folder_watch_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    /// <summary>UI thread: merges debounced watcher changes into the navigation list.</summary>
    private async Task ApplyFolderChangesAsync(string folder, FolderChangeSet set)
    {
        try
        {
            if (_windowLifetimeCts.IsCancellationRequested) return;
            var current = _navigator.Current;
            if (current is null || !string.Equals(Path.GetDirectoryName(current), folder, StringComparison.OrdinalIgnoreCase)) return;

            bool changed;
            if (set.RescanRequired)
            {
                if (Interlocked.Exchange(ref _folderRescanRunning, 1) == 1) return;
                try
                {
                    var paths = await Task.Run(() => ImageNavigator.EnumerateSupportedFolder(current));
                    current = _navigator.Current;
                    if (current is null || !string.Equals(Path.GetDirectoryName(current), folder, StringComparison.OrdinalIgnoreCase)) return;
                    if (File.Exists(current)) { _navigator.ReplaceFolderIndex(paths, current); changed = true; }
                    else
                    {
                        var added = paths.Except(_navigator.Paths, StringComparer.OrdinalIgnoreCase).ToArray();
                        var removed = _navigator.Paths.Except(paths, StringComparer.OrdinalIgnoreCase).Append(current).ToArray();
                        changed = _navigator.ApplyChanges(added, removed);
                    }
                }
                finally { Volatile.Write(ref _folderRescanRunning, 0); }
            }
            else
            {
                changed = _navigator.ApplyChanges(set.Added, set.Removed);
            }
            if (!changed) return;

            _diagnostics.Write("navigation", "folder_watch_applied", new
            {
                added = set.Added.Count, removed = set.Removed.Count, rescan = set.RescanRequired, count = _navigator.Count
            });
            var now = _navigator.Current;
            if (now is not null && !string.Equals(now, _currentPath, StringComparison.OrdinalIgnoreCase)
                && _currentPath is not null && !File.Exists(_currentPath))
            {
                // The viewed file was deleted/renamed externally; show the neighbour now in its slot.
                await PresentCurrentAsync();
            }
            UpdatePictureCounter();
            UpdateStatusStats();
            ScheduleNavigationNeighbours();
        }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "folder_watch_apply_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    private async Task NavigateFromViewportAsync(int delta)
    {
        try
        {
            _diagnostics.Write("input", "viewport_browse", new
            {
                delta, slideshowActive = _slideshow?.IsActive == true, slideshowRunning = _slideshow?.IsRunning == true,
                fullscreen = WindowState == WindowState.FullScreen, index = _navigator.Index, count = _navigator.Count
            });
            await NavigateMouseAsync(delta);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                _diagnostics.Write("navigation", "viewport_browse_failed", new { delta, error = ex.GetType().Name, ex.Message });
            }
            catch
            {
                // Diagnostics must never turn an input event into an unobserved task failure.
            }
        }
    }

    private async Task<PresentationOutcome> PresentCurrentAsync(bool rapidPreviewOnly = false, bool forceFull = false, bool? skipTransition = null)
    {
        var path = _navigator.Current;
        if (path is null)
            return new PresentationOutcome(PresentationStatus.Cancelled, default);
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("present_request", ImageFormatRegistry.GetLongestExtension(path));

        var request = BeginImageRequest(path);
        if (_wholeAppOverlayMode && !string.Equals(_wholeAppOverlayPlacementPath, path, StringComparison.OrdinalIgnoreCase))
            SaveCurrentWholeAppOverlayPlacement(_wholeAppOverlayPlacementPath);
        _currentPath = path;
        _metadataCts?.Cancel();
        _metadataCts?.Dispose();
        _metadataCts = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        _currentMetadata = new();
        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();

        try
        {
            // A warm preview can be both the cache entry and the bitmap currently attached to the
            // viewport. Detach that exact preview before an authoritative full load so AddPrepared
            // cannot dispose it while the decoder is still in flight.
            if (forceFull && Viewport.Bitmap is { } displayed &&
                string.Equals(_presentedPath, path, StringComparison.OrdinalIgnoreCase))
                _loader.DetachPreparedPreview(path, displayed);

            var presentationPolicy = rapidPreviewOnly ? BuildRapidBrowsePolicy() : BuildPerformancePolicy();
            var result = forceFull
                ? await _loader.LoadFullForegroundAsync(path, request.Token)
                : await _loader.LoadForegroundAsync(path, presentationPolicy, request.Token,
                    cachePreview: !rapidPreviewOnly);
            if (result is null || !IsRequestCurrent(request))
            {
                // Successful foreground results are retained by ImageLoadCoordinator's prepared
                // cache. Purge/eviction owns disposal; a stale continuation must not dispose that
                // bitmap a second time after standby has already purged it.
                if (_warmPresentationGateActive)
                    await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
                return new PresentationOutcome(PresentationStatus.Cancelled, request);
            }

            // Arm before assignment so even an immediately scheduled render cannot outrun registration.
            // IMPORTANT: every replacement waits for the new frame to cross the compositor fence before
            // any prior bitmap is eligible for disposal/eviction. Rapid preview frames are deliberately
            // not cached, so disposing them immediately here can otherwise race Skia's render thread and
            // hard-terminate the process with no managed exception.
            var renderedTask = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
            var previousFrame = Viewport.PresentedFrame;
            var hadBitmap = previousFrame.Bitmap is not null;
            var old = previousFrame.Bitmap;
            var retiredInteraction = _interactionPreviewBitmap;
            Viewport.SetInteractionBitmap(null);
            _interactionPreviewBitmap = null;
            Viewport.PreserveManualZoomOnBitmapChange = _settings.PreserveManualZoomOnNavigate;
            var sourceWidth = result.SourceWidth > 0 ? result.SourceWidth : result.Bitmap.PixelSize.Width;
            var sourceHeight = result.SourceHeight > 0 ? result.SourceHeight : result.Bitmap.PixelSize.Height;
            bool isRefinementOnly = forceFull && hadBitmap && string.Equals(_presentedPath, path, StringComparison.OrdinalIgnoreCase);
            if (isRefinementOnly)
            {
                Viewport.ReplaceBitmapPreservingView(result.Bitmap);
            }
            else
            {
                Viewport.SwapPresentedFrame(result.Bitmap, sourceWidth, sourceHeight, request.ImageRequestId, path);
            }
            _presentedPath = path;
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.ImagePresented,
                a: request.ImageRequestId, b: result.IsPreview ? 1 : 0,
                x: sourceWidth, y: sourceHeight, detail: Path.GetExtension(path));
            _loader.SetActivePath(path);
            if (!isRefinementOnly && (!hadBitmap || !_settings.PreserveManualZoomOnNavigate))
                Viewport.ApplyViewMode(EffectiveDefaultViewMode());
            ShowImageSurface();
            if (!isRefinementOnly)
            {
                if (_settings.AnimateGifs && GifAnimationPlayer.IsGif(path))
                    (_gifPlayer ??= new GifAnimationPlayer(Viewport, (name, data) => _diagnostics.Write("gif", name, data))).Start(path);
                else _gifPlayer?.Stop();
            }

            // A newer click may have arrived while this frame was being attached. Do not let an
            // older continuation perform workspace/status work after it has become obsolete.
            if (!IsRequestCurrent(request, requirePresented: true))
                return new PresentationOutcome(PresentationStatus.Cancelled, request);

            if (old is not null && !ReferenceEquals(old, result.Bitmap))
            {
                _diagnostics.Write("render", "bitmap_retire_wait", new
                {
                    request = request.ImageRequestId, path, oldWidth = previousFrame.SourceSize.Width, oldHeight = previousFrame.SourceSize.Height,
                    newWidth = sourceWidth, newHeight = sourceHeight, rapidPreviewOnly
                });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var safeToRetire = await renderedTask.ConfigureAwait(false);
                        _diagnostics.Write("render", "bitmap_retire_fence", new { request = request.ImageRequestId, path, safeToRetire });
                        if (safeToRetire)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (old is not null && !ReferenceEquals(old, Viewport.Bitmap) && !_loader.IsBitmapCached(old))
                                    old.Dispose();
                                if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, old) &&
                                    !ReferenceEquals(retiredInteraction, Viewport.Bitmap) && !_loader.IsBitmapCached(retiredInteraction))
                                    retiredInteraction.Dispose();
                            }, DispatcherPriority.Background);
                        }
                    }
                    catch { }
                });
            }

            if (GlidePerformanceTrace.Enabled)
            {
                GlidePerformanceTrace.Mark("bitmap_assigned",
                    $"request={request.ImageRequestId};{result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height};preview={result.IsPreview};cache={result.CacheHit};prepared={result.PreparedFrameHit};route={result.DecodeRoute}");
                // Benchmark-only negative control. The trace harness can inject a known pre-fence
                // event and must reject the row, proving its contamination gate is effective.
                if (string.Equals(Environment.GetEnvironmentVariable("GLIDE_BENCHMARK_INJECT_PREFENCE_EVENT"), "1", StringComparison.Ordinal))
                    GlidePerformanceTrace.Mark("folder_index_start", $"synthetic=true;request={request.ImageRequestId}");
            }

            if (_warmPresentationGateActive || !_startupFirstImageTimingWritten || GlidePerformanceTrace.Enabled || App.BenchmarkExitAfterFirstFrame || App.NotifyFirstFrameEventName is not null)
            {
                var rendered = await renderedTask;
                if (!rendered || !IsRequestCurrent(request, requirePresented: true))
                {
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("frame_render_fence_rejected", $"request={request.ImageRequestId}");
                    if (_warmPresentationGateActive)
                        await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
                    return new PresentationOutcome(PresentationStatus.Cancelled, request);
                }

                _startupFirstImageTimingWritten = true;

                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("composition_batch_rendered", $"request={request.ImageRequestId};path={Path.GetFileName(path)}");

                if (App.NotifyFirstFrameEventName is { } notifyName)
                {
                    try
                    {
                        using var readyEvent = EventWaitHandle.OpenExisting(notifyName);
                        readyEvent.Set();
                    }
                    catch { }
                }

                if (App.BenchmarkExitAfterFirstFrame)
                    Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);

                if (_warmPresentationGateActive)
                    await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
            }

            _startupFirstImageTimingWritten = true;

            // Everything below is nonessential for the first correct frame and is intentionally gated
            // behind the render fence for this exact request.
            _diagnostics.Enable();
            _workspace.ReplaceActiveImage(path);
            _currentPath = path;
            PublishViewportDemand();
            _currentPixelWidth = result.SourceWidth > 0 ? result.SourceWidth : result.Bitmap.PixelSize.Width;
            _currentPixelHeight = result.SourceHeight > 0 ? result.SourceHeight : result.Bitmap.PixelSize.Height;
            if (_wholeAppOverlayMode && !string.Equals(_wholeAppOverlayPlacementPath, path, StringComparison.OrdinalIgnoreCase))
                ApplyWholeAppOverlayPlacementForCurrentImage(preferRemembered: true);
            _currentDecodeMs = result.DecodeTime.TotalMilliseconds;
            _currentFirstFrameDecodeMs = result.DecodeTime.TotalMilliseconds;
            _currentFirstFrameWidth = result.Bitmap.PixelSize.Width;
            _currentFirstFrameHeight = result.Bitmap.PixelSize.Height;
            _currentDecodeRoute = result.DecodeRoute;
            _currentFrameIsPreview = result.IsPreview;
            if (!result.IsPreview) CancelPreviewQualityRecovery();
            try { _currentFileSize = new FileInfo(path).Length; } catch { _currentFileSize = 0; }

            var metadataToken = _metadataCts.Token;
            if (!rapidPreviewOnly && IsRequestCurrent(request, requirePresented: true))
                _ = LoadMetadataAfterPaintAsync(path, request, metadataToken);

            UpdatePictureCounter();
            UpdateStatusStats();
            UpdateWindowTitle();
            if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
            if (!rapidPreviewOnly && _settings.TabsEnabled) RebuildTabStrip();
            if (!rapidPreviewOnly) ScheduleNavigationNeighbours();
            if (!rapidPreviewOnly && result.IsPreview && _settings.BackgroundRefinement &&
                IsRequestCurrent(request, requirePresented: true))
                _ = RefinePresentedAsync(result, request);
            if (result.IsPreview && IsRequestCurrent(request, requirePresented: true))
                SchedulePreviewQualityRecovery(path, request.ImageRequestId);

            return new PresentationOutcome(PresentationStatus.Accepted, request);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write("decode", "foreground_cancelled", new { path, request = request.ImageRequestId });
            if (_warmPresentationGateActive)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new PresentationOutcome(PresentationStatus.Cancelled, request);
        }
        catch (Exception ex)
        {
            if (IsRequestCurrent(request))
            {
                _diagnostics.Write("decode", "foreground_failed", new { path, request = request.ImageRequestId, error = ex.GetType().Name, ex.Message });
                var extension = ImageFormatRegistry.GetLongestExtension(path);
                Title = string.IsNullOrEmpty(extension)
                    ? AppVersion.OpenFailedTitle(ex.GetType().Name)
                    : AppVersion.NoDecoderTitle(extension);
            }
            if (_warmPresentationGateActive)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new PresentationOutcome(PresentationStatus.Failed, request);
        }
    }

    private void ScheduleNavigationNeighbours()
    {
        if (!_folderIndexReady) return;
        if (_navigator.Count >= 2)
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.PrefetchStart,
                a: _navigator.Index, b: _lastNavigationDirection, x: _navigator.Count);
            _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.PrefetchEnd,
                a: _navigator.Index, b: _lastNavigationDirection, x: _navigator.Count);
        }

        if (_settings.PredictivePrefetch && _settings.PrefetchSiblingFolders && _navigator.Current is { } current)
        {
            var folder = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(folder) && !string.Equals(folder, _lastSurroundingPrefetchFolder, StringComparison.OrdinalIgnoreCase))
            {
                _lastSurroundingPrefetchFolder = folder;
                _ = WarmSurroundingFoldersAsync(current);
            }
        }
    }

    private async Task WarmSurroundingFoldersAsync(string currentPath)
    {
        var count = Math.Clamp(_settings.PrefetchSiblingFolderImageCount, 1, 30);
        var options = new SiblingFolderNavigationOptions(
            _settings.FolderNavIncludeHidden,
            _settings.FolderNavWrap,
            _settings.FolderNavSkipEmpty,
            _settings.FolderNavOpenFirstImage,
            _settings.HierarchicalFolderTraversal,
            _settings.FolderNavOrder,
            string.Equals(_settings.HierarchicalPreviousFolderEntry, "First image", StringComparison.OrdinalIgnoreCase));
        var warmed = new List<string>();
        try
        {
            foreach (var direction in new[] { -1, 1 })
            {
                var result = await SiblingFolderNavigationService.FindAsync(
                    currentPath, direction, options, forceFirstImage: true, ImageNavigator.IsSupported);
                if (result is null) continue;
                var targets = result.Images.Take(count).ToArray();
                await _loader.WarmAsync(targets);
                warmed.AddRange(targets);
            }
            _diagnostics.Write("prefetch", "surrounding_folders_warmed", new
            {
                folder = Path.GetDirectoryName(currentPath),
                requestedPerFolder = count,
                warmed = warmed.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("prefetch", "surrounding_folder_failed", new { path = currentPath, error = ex.GetType().Name });
        }
    }

    private async Task RefinePresentedAsync(ImageLoadResult firstFrame, ImageRequestContext request)
    {
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.RefinementStart,
            a: request.ImageRequestId, detail: Path.GetExtension(firstFrame.Path));
        try
        {
            var refined = await _loader.RefineForegroundAsync(firstFrame, request.Token);
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.RefinementEnd,
                a: request.ImageRequestId, b: refined is null ? 0 : 1,
                x: refined?.DecodeTime.TotalMilliseconds ?? 0, detail: Path.GetExtension(firstFrame.Path));
            if (refined is null || !IsRequestCurrent(request, requirePresented: true))
            {
                DisposeRejectedRefinement(refined);
                return;
            }

            // A bounded refinement is still a preview. Decode the authoritative full frame outside
            // the navigation gate, then serialize only the viewport/cache ownership transition.
            // Previously refinement mutated Viewport.Bitmap independently of Home/manual/slideshow
            // navigation; that gave two UI producers ownership of the same bitmap lifetime.
            if (refined.IsPreview)
            {
                DisposeRejectedRefinement(refined);
                if (!IsRequestCurrent(request, requirePresented: true)) return;

                var full = await _loader.LoadFullForegroundAsync(firstFrame.Path, request.Token, cacheResult: false);
                if (full is null || !IsRequestCurrent(request, requirePresented: true))
                {
                    if (full is not null && !_loader.IsBitmapCached(full.Bitmap)) full.Bitmap.Dispose();
                    return;
                }

                Bitmap? detachedPreview = null;
                var retiredInteraction = _interactionPreviewBitmap;
                var fence = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
                await _navigationSerializationGate.WaitAsync(request.Token).ConfigureAwait(true);
                try
                {
                    if (!IsRequestCurrent(request, requirePresented: true))
                    {
                        if (!_loader.IsBitmapCached(full.Bitmap)) full.Bitmap.Dispose();
                        return;
                    }

                    _diagnostics.Write("render", "refinement_gate_enter", new
                    { request = request.ImageRequestId, path = firstFrame.Path, mode = "full_after_bounded" });
                    Viewport.SetInteractionBitmap(null);
                    _interactionPreviewBitmap = null;
                    detachedPreview = Viewport.ReplaceBitmapPreservingView(full.Bitmap);
                }
                finally
                {
                    _navigationSerializationGate.Release();
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var rendered = await fence.ConfigureAwait(false);
                        _diagnostics.Write("render", "refinement_fence", new
                        { request = request.ImageRequestId, path = firstFrame.Path, rendered, mode = "full_after_bounded" });
                        if (!rendered || !IsRequestCurrent(request, requirePresented: true)) return;

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsRequestCurrent(request, requirePresented: true)) return;
                            var fullPromoted = _loader.PromoteRefinement(firstFrame, full);
                            if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, detachedPreview) &&
                                !ReferenceEquals(retiredInteraction, full.Bitmap) && !_loader.IsBitmapCached(retiredInteraction))
                                retiredInteraction.Dispose();

                            _interactionPreviewBitmap = fullPromoted ? null : detachedPreview;
                            Viewport.SetInteractionBitmap(_interactionPreviewBitmap);
                            _currentFrameIsPreview = false;
                            CancelPreviewQualityRecovery();
                            UpdateStatusStats();
                            if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
                            _diagnostics.Write("decode", "foreground_refined_full",
                                new { path = firstFrame.Path, request = request.ImageRequestId, cacheHit = full.CacheHit, preparedHit = full.PreparedFrameHit, promoted = fullPromoted });
                        }, DispatcherPriority.Background);
                    }
                    catch { }
                });
                return;
            }

            Bitmap? oldBitmap = null;
            var retiredInteractionDirect = _interactionPreviewBitmap;
            var directFence = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
            await _navigationSerializationGate.WaitAsync(request.Token).ConfigureAwait(true);
            try
            {
                if (!IsRequestCurrent(request, requirePresented: true))
                {
                    DisposeRejectedRefinement(refined);
                    return;
                }

                _diagnostics.Write("render", "refinement_gate_enter", new
                { request = request.ImageRequestId, path = firstFrame.Path, mode = "direct" });
                Viewport.SetInteractionBitmap(null);
                _interactionPreviewBitmap = null;
                oldBitmap = Viewport.ReplaceBitmapPreservingView(refined.Bitmap);
            }
            finally
            {
                _navigationSerializationGate.Release();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var rendered = await directFence.ConfigureAwait(false);
                    _diagnostics.Write("render", "refinement_fence", new
                    { request = request.ImageRequestId, path = firstFrame.Path, rendered, mode = "direct" });
                    if (!rendered || !IsRequestCurrent(request, requirePresented: true)) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsRequestCurrent(request, requirePresented: true)) return;
                        var promoted = _loader.PromoteRefinement(firstFrame, refined);
                        if (retiredInteractionDirect is not null && !ReferenceEquals(retiredInteractionDirect, oldBitmap) &&
                            !ReferenceEquals(retiredInteractionDirect, refined.Bitmap) && !_loader.IsBitmapCached(retiredInteractionDirect))
                            retiredInteractionDirect.Dispose();

                        _interactionPreviewBitmap = promoted ? null : oldBitmap;
                        Viewport.SetInteractionBitmap(_interactionPreviewBitmap);
                        _currentFrameIsPreview = refined.IsPreview;
                        if (!refined.IsPreview) CancelPreviewQualityRecovery();
                        _currentDecodeMs += refined.DecodeTime.TotalMilliseconds;
                        UpdateStatusStats();
                        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
                        _diagnostics.Write("decode", "foreground_refined",
                            new { path = firstFrame.Path, request = request.ImageRequestId, refineMs = refined.DecodeTime.TotalMilliseconds,
                                cacheHit = refined.CacheHit, preparedHit = refined.PreparedFrameHit, promoted });
                    }, DispatcherPriority.Background);
                }
                catch { }
            });
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write("decode", "refinement_cancelled", new { path = firstFrame.Path, request = request.ImageRequestId });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("decode", "refinement_failed",
                new { path = firstFrame.Path, request = request.ImageRequestId, error = ex.GetType().Name, ex.Message });
        }
    }

    private GifAnimationPlayer? _gifPlayer;

    private void DisposeRejectedRefinement(ImageLoadResult? result)
    {
        // Refined cache hits remain loader-owned. A newly decoded refinement is MainWindow-owned
        // until it is attached, so dispose only that uncached result when it is rejected.
        if (result is null || result.PreparedFrameHit || _loader.IsBitmapCached(result.Bitmap)) return;
        result.Bitmap.Dispose();
    }

    private void ShowNavigationBoundaryNotice(int direction)
    {
        var message = direction >= 0 ? "End of folder — no next image" : "Start of folder — no previous image";
        NavigationBoundaryNoticeText.Text = message;
        NavigationBoundaryNotice.IsVisible = true;
        StatusStatsText.Text = message;
        _navigationBoundaryNoticeCts?.Cancel();
        _navigationBoundaryNoticeCts?.Dispose();
        var cts = _navigationBoundaryNoticeCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1400, cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    NavigationBoundaryNotice.IsVisible = false;
                    // The status bar borrowed the boundary message; restore the image info.
                    if (StatusStatsText.Text == message) UpdateStatusStats();
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
        });
    }

    // Do not hold the navigation gate across foreground decoding. A previous image request can take
    // seconds on a cold codec path; waiting for it here makes a fresh click appear to be ignored and
    // prevents BeginImageRequest from cancelling the obsolete decode. NavigateCoreAsync serializes
    // only the short navigator/workspace mutation, then lets the request pipeline own cancellation.
    private enum NavigationRateInput { Mouse, Keyboard }

    private Task NavigateMouseAsync(int delta) =>
        TryAcceptNavigationRateLimit(NavigationRateInput.Mouse) ? NavigateAsync(delta) : Task.CompletedTask;

    private Task NavigateKeyboardAsync(int delta) =>
        TryAcceptNavigationRateLimit(NavigationRateInput.Keyboard) ? NavigateAsync(delta) : Task.CompletedTask;

    private bool TryAcceptNavigationRateLimit(NavigationRateInput input, bool showIndicator = true)
    {
        var intervalMs = Math.Clamp(_settings.NavigationRateLimitMs, 0, 600000);
        if (intervalMs <= 0 || !NavigationRateLimitAppliesTo(input) || !NavigationRateLimitAppliesInCurrentMode())
        {
            HideNavigationRateLimitNotice();
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastRateLimitedNavigationTimestamp == 0 ||
            Stopwatch.GetElapsedTime(_lastRateLimitedNavigationTimestamp, now) >= TimeSpan.FromMilliseconds(intervalMs))
        {
            _lastRateLimitedNavigationTimestamp = now;
            HideNavigationRateLimitNotice();
            return true;
        }

        // The interval indicator is feedback for a blocked user action. The held-browse producer
        // deliberately polls faster than the interval, so it suppresses the indicator to avoid
        // flashing "wait" for its own pacing.
        if (showIndicator && _settings.NavigationRateLimitShowIndicator)
            ShowNavigationRateLimitNotice();
        return false;
    }

    private bool NavigationRateLimitAppliesTo(NavigationRateInput input)
    {
        var choice = _settings.NavigationRateLimitInput?.Trim();
        if (string.Equals(choice, "Both", StringComparison.OrdinalIgnoreCase)) return true;
        return input == NavigationRateInput.Mouse
            ? string.Equals(choice, "Mouse only", StringComparison.OrdinalIgnoreCase)
            : string.Equals(choice, "Keyboard only", StringComparison.OrdinalIgnoreCase);
    }

    private bool NavigationRateLimitAppliesInCurrentMode()
    {
        if (_slideshow?.IsActive == true) return _settings.NavigationRateLimitSlideshow;
        if (WindowState == WindowState.FullScreen) return _settings.NavigationRateLimitFullscreen;
        return _settings.NavigationRateLimitWindowed;
    }

    private void HideNavigationRateLimitNotice()
    {
        _navigationRateLimitNoticeCts?.Cancel();
        _navigationRateLimitNoticeCts?.Dispose();
        _navigationRateLimitNoticeCts = null;
        if (NavigationRateLimitNotice.IsVisible)
            NavigationRateLimitNotice.IsVisible = false;
    }

    private void ShowNavigationRateLimitNotice()
    {
        _navigationRateLimitNoticeCts?.Cancel();
        _navigationRateLimitNoticeCts?.Dispose();
        _navigationRateLimitNoticeCts = new CancellationTokenSource();
        var token = _navigationRateLimitNoticeCts.Token;
        ApplyNavigationRateLimitNoticePosition();
        NavigationRateLimitNotice.IsVisible = true;
        _ = UpdateNavigationRateLimitNoticeAsync(token);
    }

    private void ApplyNavigationRateLimitNoticePosition()
    {
        var position = _settings.NavigationRateLimitIndicatorPosition?.Trim().ToLowerInvariant();
        NavigationRateLimitNotice.HorizontalAlignment = position is "top right" or "bottom right" ? Avalonia.Layout.HorizontalAlignment.Right : position == "centre" ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;
        NavigationRateLimitNotice.VerticalAlignment = position is "bottom left" or "bottom right" ? Avalonia.Layout.VerticalAlignment.Bottom : position == "centre" ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Top;
        NavigationRateLimitNotice.Margin = position switch
        {
            "top right" => new Thickness(0, 14, 14, 0),
            "bottom left" => new Thickness(14, 0, 0, 14),
            "bottom right" => new Thickness(0, 0, 14, 14),
            "centre" => new Thickness(0),
            _ => new Thickness(14, 14, 0, 0)
        };
    }

    private async Task UpdateNavigationRateLimitNoticeAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var intervalMs = Math.Clamp(_settings.NavigationRateLimitMs, 0, 600000);
                if (intervalMs <= 0 || _lastRateLimitedNavigationTimestamp == 0)
                    break;

                var elapsed = Stopwatch.GetElapsedTime(_lastRateLimitedNavigationTimestamp).TotalMilliseconds;
                var remaining = Math.Max(0, intervalMs - elapsed);
                if (remaining <= 0)
                    break;

                NavigationRateLimitNoticeText.Text = $"Wait {Math.Ceiling(remaining):0} ms";
                NavigationRateLimitNotice.IsVisible = _settings.NavigationRateLimitShowIndicator;
                await Task.Delay(25, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested)
                NavigationRateLimitNotice.IsVisible = false;
        }
    }

    private Task NavigateAsync(int delta) => NavigateCoreAsync(delta);

    private async Task RunSerializedNavigationAsync(Func<Task> action)
    {
        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try { await action().ConfigureAwait(true); }
        finally { _navigationSerializationGate.Release(); }
    }

    private async Task NavigateCoreAsync(int delta)
    {
        if (_navigator.Count == 0 || delta == 0 || _navigationBoundaryPromptActive) return;
        _selectionZoomDemandPath = null;
        _lastNavigationDirection = Math.Sign(delta);

        var now = Stopwatch.GetTimestamp();
        var rapid = _lastNavigationRequestTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastNavigationRequestTimestamp, now).TotalMilliseconds <= 150;
        _lastNavigationRequestTimestamp = now;
        _loader.ReportNavigationActivity(rapid ? ImageNavigationActivity.RapidBrowse : ImageNavigationActivity.NormalBrowse);
        _diagnostics.Write("navigation", "requested", new { delta, rapid, index = _navigator.Index, count = _navigator.Count, folderIndexReady = _folderIndexReady, folderIndexCanonicalReady = _folderIndexCanonicalReady });
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.NavigationRequest,
            a: delta, b: rapid ? 1 : 0, x: _navigator.Index, y: _navigator.Count);

        if (!_folderIndexReady)
        {
            await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
            try { _pendingNavigationDelta = Math.Clamp(_pendingNavigationDelta + delta, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands); }
            finally { _navigationSerializationGate.Release(); }
            return;
        }

        var atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
        if (atBoundary && !_folderIndexCanonicalReady)
        {
            await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
            try { _pendingNavigationDelta = Math.Clamp(_pendingNavigationDelta + delta, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands); }
            finally { _navigationSerializationGate.Release(); }
            return;
        }

        if (atBoundary && _settings.ContinueSiblingFolders)
        {
            // The folder index may still be a stale single-item fallback after a transient
            // enumeration failure or a late folder refresh. Before declaring a branch boundary,
            // re-read the current folder canonically once. This is boundary-only work, so ordinary
            // image-to-image navigation keeps its fast path.
            if (await RefreshCurrentFolderIndexAtBoundaryAsync())
            {
                atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
                if (!atBoundary)
                {
                    await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
                    try
                    {
                        if (_navigator.Count == 0 || _navigationBoundaryPromptActive) return;
                        atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
                        if (atBoundary) return;
                        _navigator.Move(delta);
                    }
                    finally
                    {
                        _navigationSerializationGate.Release();
                    }
                    await PresentCurrentAsync(rapidPreviewOnly: rapid, skipTransition: false);
                    if (rapid) ScheduleRapidNavigationSettle();
                    return;
                }
            }
            // Sibling lookup/presentation is itself serialized by this gate, but it is deliberately
            // entered only after the caller has reached this branch; ordinary in-folder clicks never
            // queue behind a previous image decode.
            await RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(Math.Sign(delta)));
            return;
        }

        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // Re-check after the short gate wait because another command may have moved the index.
            if (_navigator.Count == 0 || _navigationBoundaryPromptActive) return;
            atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
            if (atBoundary)
            {
                ShowNavigationBoundaryNotice(delta);
                return;
            }
            _navigator.Move(delta);
        }
        finally
        {
            _navigationSerializationGate.Release();
        }

        await PresentCurrentAsync(rapidPreviewOnly: rapid, skipTransition: false);
        if (rapid) ScheduleRapidNavigationSettle();
    }

    private async Task<bool> RefreshCurrentFolderIndexAtBoundaryAsync()
    {
        var expectedPath = _navigator.Current;
        if (expectedPath is null) return false;
        string[] paths;
        try
        {
            paths = await Task.Run(() => ImageNavigator.EnumerateSupportedFolder(expectedPath), _windowLifetimeCts.Token);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "boundary_folder_refresh_failed", new { path = expectedPath, error = ex.GetType().Name });
            return false;
        }

        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_navigationBoundaryPromptActive || !string.Equals(_navigator.Current, expectedPath, StringComparison.OrdinalIgnoreCase))
                return false;
            _navigator.ReplaceFolderIndex(paths, expectedPath);
            _folderIndexReady = true;
            _folderIndexCanonicalReady = true;
            UpdatePictureCounter();
            UpdateStatusStats();
            _diagnostics.Write("navigation", "boundary_folder_refreshed", new
            {
                path = expectedPath,
                count = _navigator.Count,
                index = _navigator.Index
            });
            ScheduleNavigationNeighbours();
            return true;
        }
        finally
        {
            _navigationSerializationGate.Release();
        }
    }

    private void ScheduleRapidNavigationSettle()
    {
        _rapidNavigationSettleCts?.Cancel();
        _rapidNavigationSettleCts?.Dispose();
        _rapidNavigationSettleCts = new CancellationTokenSource();
        var token = _rapidNavigationSettleCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await RunSerializedNavigationAsync(async () =>
                        {
                            if (token.IsCancellationRequested || _navigator.Current is null) return;
                            _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                            await PresentCurrentAsync(rapidPreviewOnly: false, forceFull: true, skipTransition: false);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _diagnostics.Write("navigation", "rapid_settle_failed", new { error = ex.GetType().Name }); }
                }, DispatcherPriority.Render);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _diagnostics.Write("navigation", "rapid_settle_failed", new { error = ex.GetType().Name }); }
        }, CancellationToken.None);
    }

    private void SchedulePreviewQualityRecovery(string path, long requestId)
    {
        _previewQualityRecoveryCts?.Cancel();
        _previewQualityRecoveryCts?.Dispose();
        var cts = _previewQualityRecoveryCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // This is a safety net, not the primary refinement path. It fires only if a preview
                // remains attached after normal refinement/rapid-settle should already have completed.
                await Task.Delay(650, token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (token.IsCancellationRequested || !_currentFrameIsPreview ||
                            !string.Equals(_presentedPath, path, StringComparison.OrdinalIgnoreCase) ||
                            Volatile.Read(ref _imageRequestId) != requestId) return;
                        await RunSerializedNavigationAsync(async () =>
                        {
                            if (token.IsCancellationRequested || !_currentFrameIsPreview ||
                                !string.Equals(_navigator.Current, path, StringComparison.OrdinalIgnoreCase)) return;
                            _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                            await PresentCurrentAsync(rapidPreviewOnly: false, forceFull: true, skipTransition: false);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _diagnostics.Write("decode", "preview_quality_recovery_failed", new { path, error = ex.GetType().Name }); }
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _diagnostics.Write("decode", "preview_quality_recovery_failed", new { path, error = ex.GetType().Name }); }
        }, CancellationToken.None);
    }

    private void CancelPreviewQualityRecovery()
    {
        _previewQualityRecoveryCts?.Cancel();
        _previewQualityRecoveryCts?.Dispose();
        _previewQualityRecoveryCts = null;
    }

    private void PublishViewportDemand()
    {
        if (_currentPath is null) return;
        var view = Viewport.CaptureViewState();
        var width = Math.Max(1, (int)Math.Ceiling(Viewport.Bounds.Width * Math.Max(1.0, RenderScaling)));
        var height = Math.Max(1, (int)Math.Ceiling(Viewport.Bounds.Height * Math.Max(1.0, RenderScaling)));
        var zoom = string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1.0, view.Zoom) : 1.0;
        var selectionZoom = string.Equals(_selectionZoomDemandPath, _currentPath, StringComparison.OrdinalIgnoreCase) ||
            (Viewport.HasSelection && string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase) && zoom > 1.0);
        var exactPixels = string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase) && Math.Abs(view.Zoom - 1.0) < 0.001;
        _loader.SetViewportDemand(_currentPath, new ImageViewportDemand(width, height, zoom, selectionZoom, exactPixels));
    }

    private async Task<bool> TryNavigateSiblingFolderAsync(int direction, bool forceFirstImage = false, bool forceDirectionalEdge = false, bool rapidPreviewOnly = false)
    {
        var navigationEpoch = _navigationCommandEpoch;
        var current = _navigator.Current;
        if (current is null || direction == 0 || _navigationBoundaryPromptActive) return false;
        SiblingFolderNavigationResult? result;
        try
        {
            result = await SiblingFolderNavigationService.FindAsync(
                current,
                direction,
                new SiblingFolderNavigationOptions(
                    _settings.FolderNavIncludeHidden,
                    _settings.FolderNavWrap,
                    _settings.FolderNavSkipEmpty,
                    forceDirectionalEdge ? direction > 0 : _settings.FolderNavOpenFirstImage,
                    _settings.HierarchicalFolderTraversal,
                    _settings.FolderNavOrder,
                    string.Equals(_settings.HierarchicalPreviousFolderEntry, "First image", StringComparison.OrdinalIgnoreCase)),
                forceFirstImage,
                ImageNavigator.IsSupported);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "sibling_folder_failed", new { path = current, error = ex.GetType().Name });
            return false;
        }
        if (result is null || navigationEpoch != _navigationCommandEpoch ||
            !string.Equals(_navigator.Current, current, StringComparison.OrdinalIgnoreCase)) return false;
        if (result.CrossedAncestorBoundary && _settings.ConfirmHierarchicalFolderTraversal)
        {
            // A blocking boundary prompt is a hard navigation barrier. Do not allow a second
            // in-flight click/wheel/key request to open another prompt or become stale work that
            // executes after the first dialog closes.
            if (_navigationBoundaryPromptActive) return false;
            var decision = await ConfirmHierarchicalFolderBoundaryAsync(result);
            if (!decision.Continue) return false;
            if (decision.DontRemindAgain)
            {
                _settings.ConfirmHierarchicalFolderTraversal = false;
                SaveSettingsAndPublish();
            }
        }
        _navigator.ReplaceFolderIndex(result.Images, result.SelectedPath);
        _folderIndexReady = true;
        _folderIndexCanonicalReady = true;
        _workspace.ReplaceActiveImage(result.SelectedPath);
        _diagnostics.Write("navigation", result.CrossedAncestorBoundary ? "hierarchical_folder" : "sibling_folder", new
        {
            direction,
            folder = result.Folder,
            path = result.SelectedPath,
            result.CrossedAncestorBoundary,
            result.BoundaryFrom,
            result.BoundaryTo
        });
        await PresentCurrentAsync(rapidPreviewOnly: rapidPreviewOnly, forceFull: !rapidPreviewOnly);
        return true;
    }

    private async Task<(bool Continue, bool DontRemindAgain)> ConfirmHierarchicalFolderBoundaryAsync(SiblingFolderNavigationResult result)
    {
        // Modal navigation prompts are command barriers, not pauses in a producer. Any rapid producer
        // that reached this boundary is cancelled, stale wheel/deferred commands are discarded, and
        // keyboard hold auto-repeat is suppressed until that physical key is released. The user may
        // continue immediately afterwards with a fresh key press, wheel event, or mouse click.
        _navigationBoundaryPromptActive = true;
        InterruptRapidNavigationForBlockingUi("hierarchical_boundary");

        var accepted = false;
        var decided = false;
        var remember = new CheckBox { Content = "Don't remind me again" };
        var proceed = new Button { Content = "Continue", MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        var dialog = CreateContentSizedDialog("Continue to nearby folder?", 460, 640);
        PopupPlacementStore.Track(dialog, "folder-boundary-confirm");
        proceed.Click += (_, _) => { accepted = true; decided = true; dialog.Close(); };
        cancel.Click += (_, _) => { accepted = false; decided = true; dialog.Close(); };
        dialog.KeyUp += (_, e) =>
        {
            if ((e.Key == Key.Right && _suppressedHeldNavigationDirection > 0) ||
                (e.Key == Key.Left && _suppressedHeldNavigationDirection < 0))
                _suppressedHeldNavigationDirection = 0;
        };
        var from = string.IsNullOrWhiteSpace(result.BoundaryFrom) ? "the current branch" : result.BoundaryFrom;
        var to = string.IsNullOrWhiteSpace(result.Folder) ? "the nearby branch" : result.Folder;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 13,
            Children =
            {
                new TextBlock { Text = "End of this folder branch", FontSize = 19, FontWeight = FontWeight.SemiBold },
                // Long folder paths can wrap to many lines; scroll the message instead of pushing
                // the decision buttons out of the dialog on small screens.
                new ScrollViewer
                {
                    MaxHeight = 300,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = $"Glide has reached the end of {from}. Continue into the nearby folder branch:\n{to}", TextWrapping = TextWrapping.Wrap }
                },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, proceed } }
            }
        };
        try
        {
            await ShowOwnedDialogAsync(dialog);
            return decided ? (accepted, accepted && remember.IsChecked == true) : (false, false);
        }
        finally
        {
            _navigationBoundaryPromptActive = false;
            _lastWheelNavigationTimestamp = 0;
            _lastNavigationRequestTimestamp = 0;
        }
    }

    private void InterruptRapidNavigationForBlockingUi(string reason)
    {
        unchecked { _navigationCommandEpoch++; }
        if (_heldNavigationKeyIsDown && _heldNavigationDirection != 0)
            _suppressedHeldNavigationDirection = _heldNavigationDirection;

        // Cancellation makes a held-arrow producer terminate at the prompt instead of resuming
        // from the destination after Continue is clicked. A fresh physical action is required.
        StopHeldNavigation(settle: false);

        // Wheel hardware and very fast mouse input can have several already-issued requests. Those
        // requests describe the pre-dialog interaction and must never execute after a modal decision.
        lock (_wheelNavigationSync)
            _wheelNavigationQueue.Clear();

        _pendingNavigationDelta = 0;
        _lastWheelNavigationTimestamp = 0;
        _lastNavigationRequestTimestamp = 0;
        _rapidNavigationSettleCts?.Cancel();
        _diagnostics.Write("navigation", "rapid_input_interrupted", new { reason, maxBuffered = MaxBufferedNavigationCommands });
    }

    private void ShowImageSurface()
    {
        CancelBrowserThumbnailWork();
        HomeHost.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = false;
        BrowserView.IsVisible = false;
        ImageView.IsVisible = true;
        ApplyStatusVisibility();
    }

    private void ShowWarmOpenTransitionSurface()
    {
        CancelBrowserThumbnailWork();
        HomeHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = true;
        ApplyStatusVisibility();
    }

    private void ArmWarmPresentationGate()
    {
        if (_warmPresentationGateActive) return;
        _warmPresentationGateActive = true;
        // Window.Opacity is the top-level compositor gate. Avalonia visual visibility changes alone
        // do not prevent DWM from briefly reusing the previous composed image on a warm HWND.
        Opacity = 0;
        if (_windowsBrowserFrame is not null && TryGetViewportColorRef(out var viewportColorRef))
            _windowsBrowserFrame.ShowPrivacyCurtain(viewportColorRef);
    }

    private bool TryGetViewportColorRef(out uint colorRef)
    {
        colorRef = 0;
        var brush = Viewport.Background as SolidColorBrush ??
            Application.Current?.Resources["BrushViewport"] as SolidColorBrush;
        if (brush is null) return false;
        var color = brush.Color;
        // Win32 COLORREF is 0x00BBGGRR, while Avalonia stores channels as RRGGBB.
        colorRef = (uint)(color.R | (color.G << 8) | (color.B << 16));
        return color.A != 0;
    }

    private async Task ReleaseWarmPresentationGateAsync(bool safeFrameReady)
    {
        if (!_warmPresentationGateActive) return;

        if (!safeFrameReady)
        {
            // An open failure/cancellation must not reveal a blank viewer. Compose the ordinary Home
            // surface first, then reveal it using the user's configured whole-window opacity.
            ShowHomeSurface();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        if (_warmPresentationGateActive)
        {
            Opacity = _sessionOpacity;
            _windowsBrowserFrame?.HidePrivacyCurtain();
            _warmPresentationGateActive = false;
        }
    }

    private HomeSurface EnsureHomeSurface()
    {
        if (_homeSurface is not null) return _homeSurface;
        var home = new HomeSurface();
        _homeSurface = home;
        HomeHost.Content = home;
        home.AddHandler(InputElement.PointerPressedEvent, HomeBackgroundPointerPressed, RoutingStrategies.Tunnel, true);
        home.StatusSurface.AddHandler(InputElement.PointerPressedEvent, StatusSurfacePointerPressed, RoutingStrategies.Tunnel, true);
        home.OpenFileRequested += async (_, _) => await ExecuteCommandAsync(GlideCommand.OpenFile);
        home.ResizeRecentRequested += (_, _) => ResizeRecentHistoryClicked(home, new RoutedEventArgs());
        home.ClearRecentRequested += (_, _) => ClearRecentHistoryClicked(home, new RoutedEventArgs());
        home.CloseRecentRequested += (_, _) => CloseRecentHistoryClicked(home, new RoutedEventArgs());
        home.SettingsRequested += (_, _) => SettingsClicked(home, new RoutedEventArgs());
        home.CollapseStatusRequested += (_, _) => CollapseStatusClicked(home, new RoutedEventArgs());
        home.CloseStatusRequested += (_, _) => CloseStatusClicked(home, new RoutedEventArgs());
        home.OptionsButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        home.StatusCollapseButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        ApplyWelcomeLayout();
        ApplyStatusBarSize();
        return home;
    }

    private void ShowHomeSurface()
    {
        _gifPlayer?.Stop();
        AdvanceWorkspaceEpoch();
        CancelBrowserThumbnailWork();
        _presentedPath = null;
        StopSlideshow(restoreStartingMode: false);
        var home = EnsureHomeSurface();
        HomeHost.IsVisible = true;
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = false;
        var recentLanding = string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase);
        home.HeadingText.Text = recentLanding ? "Recent pictures" : "Glide";
        home.SubtitleText.Text = recentLanding
            ? "Jump straight back into recently opened images."
            : "Open an image with Ctrl+O or drop one here, or pick up where you left off below.";
        ApplyWelcomeLayout();
        Title = recentLanding ? AppVersion.RecentTitle : AppVersion.HomeTitle;
        RefreshRecentHistoryHome();
        ApplyStatusVisibility();
    }

    private BrowserSurface EnsureBrowserSurface()
    {
        if (_browserSurface is not null) return _browserSurface;
        var browser = new BrowserSurface();
        _browserSurface = browser;
        BrowserView.Content = browser;
        _browserUiReady = false;
        _browserViewMode = Math.Max(0, browser.ViewModeCombo.SelectedIndex);
        browser.Repeater.ItemTemplate = new FuncDataTemplate<BrowserEntry>((entry, _) => CreateBrowserItem(entry), supportsRecycling: false);
        browser.Repeater.ElementPrepared += BrowserRepeaterElementPrepared;
        browser.Repeater.ElementClearing += BrowserRepeaterElementClearing;
        browser.BackRequested += (_, _) => BrowserBackClicked(browser, new RoutedEventArgs());
        browser.ForwardRequested += (_, _) => BrowserForwardClicked(browser, new RoutedEventArgs());
        browser.UpRequested += (_, _) => BrowserUpClicked(browser, new RoutedEventArgs());
        browser.RefreshRequested += (_, _) => BrowserRefreshClicked(browser, new RoutedEventArgs());
        browser.AddressKeyPressed += (_, e) => BrowserAddressKeyDown(browser.AddressBox, e);
        browser.ViewModeChanged += (_, e) => BrowserViewModeChanged(browser.ViewModeCombo, e);
        browser.WindowsExplorerRequested += (_, _) => BrowserWindowsExplorerClicked(browser, new RoutedEventArgs());
        browser.BrowserKeyPressed += (_, e) => BrowserListKeyDown(browser.ScrollViewer, e);
        _browserUiReady = true;
        ConfigureBrowserRepeaterLayout();
        RefreshDynamicTooltips();
        ApplyManagedExplorerTheme();
        return browser;
    }

    private Button BrowserBackButton => EnsureBrowserSurface().BackButton;
    private Button BrowserForwardButton => EnsureBrowserSurface().ForwardButton;
    private Button BrowserUpButton => EnsureBrowserSurface().UpButton;
    private Button BrowserRefreshButton => EnsureBrowserSurface().RefreshButton;
    private TextBox BrowserAddressBox => EnsureBrowserSurface().AddressBox;
    private ComboBox BrowserViewModeCombo => EnsureBrowserSurface().ViewModeCombo;
    private ScrollViewer BrowserScrollViewer => EnsureBrowserSurface().ScrollViewer;
    private ItemsRepeater BrowserRepeater => EnsureBrowserSurface().Repeater;
    private UniformGridLayout BrowserRepeaterLayout => EnsureBrowserSurface().RepeaterLayout;
    private Grid NativeExplorerHostSurface => EnsureBrowserSurface().NativeHostSurface;

    private void ShowBrowserSurface()
    {
        AdvanceWorkspaceEpoch();
        var browserSurface = EnsureBrowserSurface();
        _presentedPath = null;
        StopSlideshow(restoreStartingMode: false);
        HomeHost.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = true;
        // Glide owns its production Explorer surface here. The legacy IExplorerBrowser host remains
        // dormant diagnostic code only; there is no production entry point to it. The managed surface
        // is authoritative because it can follow Glide theme/resizing consistently across Windows/DPI.
        BrowserScrollViewer.IsVisible = true;
        ApplyManagedExplorerTheme();
        ApplyStatusVisibility();
    }

    /// <summary>
    /// Native Shell COM objects are created only after a Browser tab becomes the active workspace.
    /// Keeping the factory here also makes the startup contract auditable: constructing MainWindow
    /// or a Home/Image tab cannot instantiate ExplorerBrowser.
    /// </summary>
    private void EnsureNativeExplorerHost(bool recreateIfFailed = false)
    {
        if (NativeExplorerHost is not null && recreateIfFailed && NativeExplorerHost.State == ExplorerHostState.Failed)
        {
            // NativeControlHost owns a real child HWND. A failed host remains an opaque native window
            // and can physically cover Avalonia's managed fallback even when BrowserScrollViewer.IsVisible=true.
            // Detach the failed child so Refresh/address navigation can make a clean COM attempt.
            NativeExplorerHost.FolderNavigated -= NativeExplorerFolderNavigated;
            NativeExplorerHost.StateChanged -= NativeExplorerStateChanged;
            NativeExplorerHost.FileActivated -= NativeExplorerFileActivated;
            NativeExplorerHostSurface.Children.Remove(NativeExplorerHost);
            NativeExplorerHost = null;
        }
        if (NativeExplorerHost is not null) return;
        NativeExplorerHost = new WindowsExplorerHost
        {
            // Keep the native HWND at 1x1 until Explorer has produced a real view. Native child HWNDs
            // always sit above Avalonia-rendered content, so this makes the managed fallback genuinely
            // visible while COM creation/navigation is in flight instead of showing a black rectangle.
            Width = 1,
            Height = 1,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        NativeExplorerHost.FolderNavigated += NativeExplorerFolderNavigated;
        NativeExplorerHost.StateChanged += NativeExplorerStateChanged;
        NativeExplorerHost.Diagnostic += (name, data) => _diagnostics.Write("native_explorer", name, data);
        NativeExplorerHost.CanActivateFile = ImageNavigator.IsSupported;
        NativeExplorerHost.FileActivated += NativeExplorerFileActivated;
        NativeExplorerHostSurface.Children.Add(NativeExplorerHost);
        ApplyEmbeddedExplorerTheme();
        ApplyNativeExplorerSurfaceOwnership(NativeExplorerHost.State);
    }

    private void ApplyNativeExplorerSurfaceOwnership(ExplorerHostState state)
    {
        if (NativeExplorerHost is null)
        {
            BrowserScrollViewer.IsVisible = true;
            return;
        }

        var ready = state == ExplorerHostState.Ready;
        BrowserScrollViewer.IsVisible = !ready;
        // Keep the host attached (1x1 when not ready) so a failure does not trigger an Avalonia
        // visibility destroy/recreate loop. The 1x1 child cannot obscure the managed fallback.
        NativeExplorerHost.IsVisible = true;
        if (ready)
        {
            NativeExplorerHost.ClearValue(WidthProperty);
            NativeExplorerHost.ClearValue(HeightProperty);
            NativeExplorerHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            NativeExplorerHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }
        else
        {
            NativeExplorerHost.Width = 1;
            NativeExplorerHost.Height = 1;
            NativeExplorerHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            NativeExplorerHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        _diagnostics.Write("native_explorer", "surface_owner_changed", new
        {
            state = state.ToString(),
            fallbackVisible = BrowserScrollViewer.IsVisible,
            nativeVisible = NativeExplorerHost.IsVisible,
            nativeReady = ready
        });
    }

    private void NativeExplorerStateChanged(ExplorerHostState state)
    {
        // The real HWND only expands to cover the browser after both view creation and navigation
        // complete. Failure removes the opaque child from sight immediately, leaving a usable fallback.
        if (!BrowserView.IsVisible) return;
        ApplyNativeExplorerSurfaceOwnership(state);
    }

    private async Task ActivateWorkspaceAsync()
    {
        if (_tabMruCycleSnapshot is null) TouchTabMru();
        switch (_workspace.Active)
        {
            case HomeTabState:
                ShowHomeSurface();
                break;
            case ImageTabState image:
                AdvanceWorkspaceEpoch();
                await LoadPathAsync(image.Path, updateActiveTab: false);
                if (image.ViewState is { } view)
                    Viewport.RestoreViewState(new ViewerViewSnapshot(view.Mode, view.Zoom, new Vector(view.PanX, view.PanY)));
                break;
            case BrowserTabState browser:
                // Compatibility migration only: the built-in Explorer no longer exists.
                _browserSessions.Remove(browser.Id);
                _browserHighlightTargets.Remove(browser.Id);
                _tabForwardFolderTargets.Remove(browser.Id);
                _tabForwardImageTargets.Remove(browser.Id);
                _workspace.ReplaceTab(new HomeTabState(browser.Id));
                ShowHomeSurface();
                _diagnostics.Write("browser", "legacy_browser_tab_migrated_to_home", new { browser.Id });
                break;
        }
        RebuildTabStrip();
        if (_workspace.Active is { } active) SelectBrowserHighlight(active.Id);
        UpdateTabNavigationButtons();
    }

    private void CaptureActiveTabRuntimeState()
    {
        if (_workspace.Active is { } active) CaptureTabRuntimeState(active.Id);
    }

    /// <summary>
    /// Commits window-owned runtime state into the framework-free tab record before a tab switches
    /// windows. This keeps transfer atomic: the target never has to reach back into source visuals.
    /// </summary>
    private void CaptureTabRuntimeState(Guid id)
    {
        var tab = _workspace.Tabs.FirstOrDefault(candidate => candidate.Id == id);
        switch (tab)
        {
            case ImageTabState image when _workspace.Active?.Id == id &&
                                          string.Equals(_currentPath, image.Path, StringComparison.OrdinalIgnoreCase):
            {
                var view = Viewport.CaptureViewState();
                _workspace.ReplaceTab(image with
                {
                    ViewState = new ImageTabViewState(view.Mode, view.Zoom, view.Pan.X, view.Pan.Y)
                });
                break;
            }
            case BrowserTabState browser when _browserSessions.TryGetValue(id, out var session):
                _workspace.ReplaceTab(browser with
                {
                    Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index, session.Highlights.ToArray())
                });
                break;
        }
    }

    private void RestoreBrowserNavigationState(BrowserTabState browser)
    {
        if (browser.Navigation is not { } navigation || _browserSessions.ContainsKey(browser.Id)) return;
        var session = new BrowserSession();
        for (var i = 0; i < navigation.History.Count; i++)
        {
            var folder = navigation.History[i];
            if (!Directory.Exists(folder)) continue;
            session.History.Add(Path.GetFullPath(folder));
            var highlight = navigation.Highlights is { } highlights && i < highlights.Count ? highlights[i] : null;
            session.Highlights.Add(!string.IsNullOrWhiteSpace(highlight) ? highlight : null);
        }
        session.Index = session.History.Count == 0
            ? -1
            : Math.Clamp(navigation.Index, 0, session.History.Count - 1);
        _browserSessions[browser.Id] = session;
    }

    private void ApplyCompactChromeLayout()
    {
        if (ChromeLayout is null || _wholeAppOverlayMode) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        // Title-bar utility actions yield one icon at a time as the window narrows (starting from
        // the last configured position) and return one at a time as it widens, instead of the whole
        // strip disappearing at a single threshold. Tabs keep the remaining space; the Windows
        // caption buttons stay protected as one unit.
        var order = _settings.TitleBarButtons;
        var actionCount = order.Count;
        const double captionWidth = 3 * 46;  // Minimize + Maximize + Close
        const double chromeMargins = 8 + 6;  // ChromeLayout left margin + right group margin
        const double minimumTabSpace = 170;  // Keeps the active tab and new-tab button usable
        const double perActionButton = 37;   // 36 px button + 1 px spacing
        var availableForActions = width - captionWidth - chromeMargins - minimumTabSpace;
        var visibleActions = actionCount == 0
            ? 0
            : Math.Clamp((int)Math.Floor((availableForActions + 1) / perActionButton), 0, actionCount);
        for (var i = 0; i < actionCount; i++)
            if (_titleBarButtons.TryGetValue(order[i], out var button))
                button.IsVisible = i < visibleActions;
        TitleActionHost.IsVisible = visibleActions > 0;
        TabNavigationHost.IsVisible = false;
        // Caption controls are protected as one Windows-standard unit. At narrow widths tabs keep
        // compressing instead of sacrificing Minimize/Maximize/Close individually.
        CaptionMinimizeButton.IsVisible = true;
        CaptionMaximizeButton.IsVisible = true;
        CaptionCloseButton.IsVisible = true;
        NewTabButton.IsVisible = _settings.TabsEnabled && width >= 360;
    }
}
