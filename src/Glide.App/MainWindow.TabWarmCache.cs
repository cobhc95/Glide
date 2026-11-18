using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Diagnostics;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Settings;
using Glide.Core.Workspace;
using Glide.Imaging;

namespace Glide.App;

/// <summary>
/// 5.1.0 instant tab switching. Switching back to a tab shows what it showed when it was left, at once, like a web
/// browser, instead of reloading it:
/// <list type="bullet">
/// <item>Image tabs (last <see cref="WarmImageTabCapacity"/>): the frame that was on screen (one extra loader lease, so
/// the decoded-frame cache cannot dispose it), the folder list with the position and the zoom / pan. Activation
/// presents it synchronously; the folder is then re-read in the background (the folder watcher was retargeted while
/// the tab was hidden) and a preview frame is refined as usual.</item>
/// <item>Explorer tabs (last <see cref="WarmExplorerTabCapacity"/>): the folder listing. The surface itself is no
/// longer retired when a picture or the Welcome page is shown, so returning to the same Explorer tab only shows it
/// again; another recent Explorer tab is rebuilt from its cached listing and then reconciled in place.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    internal const int WarmImageTabCapacity = 5;
    internal const int WarmExplorerTabCapacity = 3;
    /// <summary>Frames above this size (a full-resolution refine of a huge picture) are not kept per tab.</summary>
    internal const long WarmImageFrameMaxBytes = 64L * 1024 * 1024;

    private sealed record WarmImageTab(
        string Path, Bitmap Bitmap, int SourceWidth, int SourceHeight, string[] FolderPaths,
        ViewerViewSnapshot View, bool IsPreview, double DecodeMs, string DecodeRoute, long FileSize);

    private sealed record WarmExplorerTab(string Folder, List<ExplorerEntry> Entries);

    private TabWarmCache<WarmImageTab>? _warmImageTabs;
    private readonly TabWarmCache<WarmExplorerTab> _warmExplorerTabs = new(WarmExplorerTabCapacity);
    /// <summary>The tab the viewer / Explorer surface was last shown for (the tab being left on the next activation).</summary>
    private Guid _shownTabId;

    private TabWarmCache<WarmImageTab> WarmImageTabs =>
        _warmImageTabs ??= new TabWarmCache<WarmImageTab>(WarmImageTabCapacity, entry => _loader.ReleaseBitmap(entry.Bitmap));

    /// <summary>Called at the start of every activation: keeps the state of the tab that is being left.</summary>
    private void CaptureLeavingTabForWarmSwitch(Guid activatingId)
    {
        var openIds = _workspace.Tabs.Select(tab => tab.Id).ToHashSet();
        _warmImageTabs?.RetainOnly(openIds);
        _warmExplorerTabs.RetainOnly(openIds);
        var leaving = _shownTabId;
        if (leaving == Guid.Empty || leaving == activatingId) return;
        switch (_workspace.Tabs.FirstOrDefault(tab => tab.Id == leaving))
        {
            case ImageTabState image:
                CaptureWarmImageTab(image);
                break;
        }
    }

    /// <summary>The Explorer surface switches to another tab: keep the listing it showed for a quick return.</summary>
    private void KeepWarmExplorerListing()
    {
        if (_explorerDisplayedTabId != Guid.Empty && _explorerLoadedFolder is { } folder && !_explorerListing)
            _warmExplorerTabs.Put(_explorerDisplayedTabId, new WarmExplorerTab(folder, _explorerAllEntries));
    }

    private void CaptureWarmImageTab(ImageTabState image)
    {
        var frame = Viewport.PresentedFrame;
        if (_speedBoostStandbyActive || frame.Bitmap is not { } bitmap || _presentedPath is not { } path ||
            !string.Equals(path, image.Path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_navigator.Current, path, StringComparison.OrdinalIgnoreCase) ||
            (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 > WarmImageFrameMaxBytes ||
            // Only a frame that already carries a presentation lease can take a second one (an unleased, uncached
            // frame is disposed by its single release).
            _loader.GetLeaseCount(bitmap) < 1)
        {
            WarmImageTabs.Remove(image.Id);
            return;
        }
        var reuse = WarmImageTabs.TryGet(image.Id, out var existing) && ReferenceEquals(existing.Bitmap, bitmap);
        if (!reuse) _loader.AddLease(bitmap);
        var view = Viewport.CaptureViewState();
        WarmImageTabs.Put(image.Id, new WarmImageTab(path, bitmap,
            frame.SourceSize.Width > 0 ? (int)frame.SourceSize.Width : bitmap.PixelSize.Width,
            frame.SourceSize.Height > 0 ? (int)frame.SourceSize.Height : bitmap.PixelSize.Height,
            _navigator.Paths.ToArray(), view, _currentFrameIsPreview, _currentDecodeMs, _currentDecodeRoute, _currentFileSize),
            (older, newer) => ReferenceEquals(older.Bitmap, newer.Bitmap));
        if (!string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase)) ShrinkWarmImageFrameLater(image.Id, bitmap);
    }

    /// <summary>
    /// 5.1.1 RAM: a left tab keeps its frame at the physical fit size (either orientation, <see cref="FrameMemoryBudget.ShrinkTarget"/>)
    /// instead of a full frame of up to 64 MB. The copy is made in the background and swapped in only while the tab still
    /// keeps the same frame and is not shown; switching back shows it at once and the "warm_tab" guard
    /// (EnsurePresentedFrameSufficient) plus the preview recovery fetch full detail when the view needs it.
    /// </summary>
    private void ShrinkWarmImageFrameLater(Guid tabId, Bitmap bitmap)
    {
        var (boxWidth, boxHeight) = PhysicalViewportForDecode();
        var target = FrameMemoryBudget.ShrinkTarget(bitmap.PixelSize.Width, bitmap.PixelSize.Height, boxWidth, boxHeight);
        if (target.Width <= 0) return;
        _loader.AddLease(bitmap); // alive while it is read; released below
        _ = Task.Run(() =>
        {
            Bitmap? scaled = null;
            try { scaled = FrameDownscaler.TryAreaDownscale(bitmap, new Avalonia.PixelSize(target.Width, target.Height), maxWorkers: 1); }
            catch { /* best effort: the tab keeps its full frame */ }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (scaled is null) return;
                    if (_warmImageTabs is null || !_warmImageTabs.Contains(tabId) || _shownTabId == tabId ||
                        !_warmImageTabs.TryGet(tabId, out var warm) || !ReferenceEquals(warm.Bitmap, bitmap))
                    {
                        scaled.Dispose();
                        return;
                    }
                    // The warm cache's own lease on the reduced copy (released by its eviction, like any warm frame).
                    _loader.AddLease(scaled);
                    _warmImageTabs.Put(tabId, warm with { Bitmap = scaled, IsPreview = true, DecodeRoute = DisplayFrameSufficiency.ReducedFrameRoute });
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("tab_warm_frame_shrunk", $"{target.Width}x{target.Height}");
                }
                finally { _loader.ReleaseBitmap(bitmap); }
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// Shows a recently left image tab synchronously from its kept frame. False when nothing usable is kept (the
    /// caller then loads the picture the ordinary way).
    /// </summary>
    private bool TryShowWarmImageTab(ImageTabState image)
    {
        if (_warmImageTabs is null || !_warmImageTabs.TryGet(image.Id, out var warm)) return false;
        if (!string.Equals(warm.Path, image.Path, StringComparison.OrdinalIgnoreCase) || _speedBoostStandbyActive ||
            _warmPresentationGateActive || _wholeAppOverlayMode || !File.Exists(warm.Path))
        {
            _warmImageTabs.Remove(image.Id);
            return false;
        }
        var started = Stopwatch.GetTimestamp();
        var path = warm.Path;
        var folder = Path.GetDirectoryName(path) ?? string.Empty;

        // Folder session exactly as LoadPathAsync starts one, but with the kept list instead of a single entry.
        _folderIndexCts?.Cancel();
        _folderIndexCts?.Dispose();
        _folderIndexCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var session = new FolderIndexSession(image.Id, Volatile.Read(ref _workspaceEpoch),
            Interlocked.Increment(ref _folderSessionId), folder, _folderIndexCts.Token);
        _pendingNavigationDelta = 0;
        _lastNavigationDirection = 0;
        if (warm.FolderPaths.Length > 0) _navigator.ReplaceFolderIndex(warm.FolderPaths, path);
        else _navigator.OpenSingleOnly(path);
        _folderIndexReady = true;
        _folderIndexCanonicalReady = true;
        EnsureFolderWatch(folder);

        var request = BeginImageRequest(path);
        _currentPath = path;
        _metadataCts?.Cancel();
        _metadataCts?.Dispose();
        _metadataCts = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        _currentMetadata = new();

        var previous = Viewport.PresentedFrame.Bitmap;
        var retiredInteraction = _interactionPreviewBitmap;
        Viewport.SetInteractionBitmap(null);
        _interactionPreviewBitmap = null;
        var renderedTask = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
        var sameFrame = ReferenceEquals(previous, warm.Bitmap);
        // The presentation takes its own lease (the warm cache keeps the other one); the frame that is still attached
        // from this very tab already has one.
        if (!sameFrame) _loader.AddLease(warm.Bitmap);
        Viewport.SwapPresentedFrame(warm.Bitmap, warm.SourceWidth, warm.SourceHeight, request.ImageRequestId, path);
        _presentedPath = path;
        _loader.SetActivePath(path);
        ShowImageSurface();
        Viewport.RestoreViewState(warm.View);
        if (_settings.AnimateGifs && GifAnimationPlayer.IsGif(path)) EnsureGifPlayer().Start(path);
        else _gifPlayer?.Stop();
        UpdateGifStatusUi();

        if (previous is not null && !sameFrame)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await renderedTask.ConfigureAwait(false))
                        Dispatcher.UIThread.Post(() =>
                        {
                            RetirePresentedBitmap(previous);
                            if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, previous)) RetirePresentedBitmap(retiredInteraction);
                        }, DispatcherPriority.Background);
                }
                catch { /* retiring the old frame is cleanup; the tab is already shown */ }
            });
        }
        else if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, previous))
        {
            RetirePresentedBitmap(retiredInteraction);
        }

        _currentPixelWidth = warm.SourceWidth;
        _currentPixelHeight = warm.SourceHeight;
        _currentDecodeMs = warm.DecodeMs;
        _currentFirstFrameDecodeMs = warm.DecodeMs;
        _currentFirstFrameWidth = warm.Bitmap.PixelSize.Width;
        _currentFirstFrameHeight = warm.Bitmap.PixelSize.Height;
        _currentDecodeRoute = warm.DecodeRoute;
        _currentFrameIsPreview = warm.IsPreview;
        _currentFileSize = warm.FileSize;
        PublishViewportDemand();
        NotifyViewerStatusChanged();
        UpdateWindowTitle();
        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
        _ = LoadMetadataAfterPaintAsync(path, request, _metadataCts.Token);
        ScheduleNavigationNeighbours();
        if (warm.IsPreview) SchedulePreviewQualityRecovery(path, request.ImageRequestId);
        Dispatcher.UIThread.Post(() => EnsurePresentedFrameSufficient("warm_tab"), DispatcherPriority.Background);
        _ = ReconcileWarmImageFolderAsync(path, session);

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("tab_warm_image_shown", $"ms={ms:F2}");
        _diagnostics.Write("navigation", "tab_warm_image_shown", new { ms, count = _navigator.Count, preview = warm.IsPreview });
        return true;
    }

    /// <summary>The kept folder list may be stale (the watcher followed another folder meanwhile): re-read it quietly.</summary>
    private async Task ReconcileWarmImageFolderAsync(string path, FolderIndexSession session)
    {
        try
        {
            var paths = await Task.Run(() => ImageNavigator.EnumerateSupportedFolder(path).ToList(), session.Token);
            if (!IsFolderSessionCurrent(session) || _navigator.Current is not { } current) return;
            if (paths.Count == _navigator.Count && paths.SequenceEqual(_navigator.Paths, StringComparer.OrdinalIgnoreCase)) return;
            if (!paths.Contains(current, StringComparer.OrdinalIgnoreCase)) return; // the watcher handles a vanished current file
            _navigator.ReplaceFolderIndex(paths, current);
            NotifyViewerStatusChanged();
            ScheduleNavigationNeighbours();
        }
        catch (OperationCanceledException) { /* a newer activation or navigation owns the folder now */ }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "tab_warm_reconcile_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    /// <summary>The kept listing of a recently left Explorer tab for <paramref name="folder"/>, if any.</summary>
    private List<ExplorerEntry>? TakeWarmExplorerListing(Guid tabId, string folder) =>
        _warmExplorerTabs.TryGet(tabId, out var warm) && string.Equals(warm.Folder, folder, StringComparison.OrdinalIgnoreCase)
            ? warm.Entries : null;

    private void ClearWarmTabs()
    {
        _warmImageTabs?.Clear();
        _warmExplorerTabs.Clear();
    }
}
