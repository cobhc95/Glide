using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Round 3 #15: in-place updates of the shown Explorer folder. Glide's own operations (paste, copy / move in, delete,
/// rename, new folder, F5) and outside changes reported by the folder watcher both re-read the folder in the background
/// and apply only the difference to the shown list (<see cref="ListingDiff"/>), the way Windows Explorer does: a removed
/// tile disappears and the following ones shift up, a new item appears where the sort puts it, and every other tile keeps
/// its element, thumbnail, selection and the scroll position.
/// </summary>
public partial class MainWindow
{
    private FolderWatchService? _explorerFolderWatch;
    private long _explorerInPlaceVersion;
    // Round 3 #15: while an in-place update settles, ItemsRepeater may clear realized tiles and hand their elements to
    // other items (Avalonia 11's repeater has no key mapping), so tiles of unchanged items are rebuilt. Their decoded
    // thumbnails are parked here instead of disposed, and the rebuilt tile shows them at once (no reload, no blank tile).
    private Dictionary<string, Bitmap>? _explorerParkedThumbnails;
    private int _explorerParkHolds;

    private static string ParkKey(ExplorerEntry entry, int mode) => $"{entry.Path}|{entry.Modified.Ticks}|{entry.Size}|{mode}";

    /// <summary>Drops a tile's thumbnail pixels: parked during an in-place update, disposed otherwise.</summary>
    private void ReleaseExplorerTileImages(Control element)
    {
        foreach (var image in element.GetVisualDescendants().OfType<Image>())
        {
            if (_explorerParkedThumbnails is { } parked && element is ExplorerTile { Tag: ExplorerEntry entry } tile &&
                image.Source is Bitmap bitmap && image.Tag is string)
            {
                var key = ParkKey(entry, tile.BuiltMode);
                if (parked.Remove(key, out var older) && !ReferenceEquals(older, bitmap)) older.Dispose();
                parked[key] = bitmap;
            }
            else if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
    }

    private Bitmap? TakeParkedExplorerThumbnail(ExplorerEntry entry, int mode) =>
        _explorerParkedThumbnails is { } parked && parked.Remove(ParkKey(entry, mode), out var bitmap) ? bitmap : null;

    /// <summary>After the layout passes that follow an in-place update, disposes the parked thumbnails nobody took back.</summary>
    private async Task ReleaseParkedExplorerThumbnailsAsync()
    {
        _explorerParkHolds++;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            if (--_explorerParkHolds == 0 && _explorerParkedThumbnails is { } parked)
            {
                _explorerParkedThumbnails = null;
                foreach (var bitmap in parked.Values) bitmap.Dispose();
            }
        }
    }

    /// <summary>Above this many single steps the shown list is swapped at once (a mass change, e.g. after a watcher overflow).</summary>
    private const int ExplorerInPlaceMaxSteps = 4000;

    private async Task ReloadExplorerFolderInPlaceAsync(string folder)
    {
        var version = Interlocked.Increment(ref _explorerInPlaceVersion);
        var generation = Volatile.Read(ref _browserGeneration);
        var activeTabId = (_workspace.Active as BrowserTabState)?.Id ?? Guid.Empty;
        var pendingSelection = _explorerPendingSelection;
        _explorerPendingSelection = null;
        var showHidden = ExplorerOptionsView.ShowHiddenFiles;
        var onlyImages = ExplorerOptionsView.ShowOnlyImages;
        var probeDimensions = ExplorerSortFields.NeedsProbe(_explorerSortKey);
        var probeKey = _explorerSortKey;
        var known = _explorerAllEntries.ToList();
        var token = _windowLifetimeCts.Token;
        try
        {
            var entries = await Task.Run(() =>
            {
                var listed = ExplorerModel.Enumerate(folder, showHidden, onlyImages, token);
                // Unchanged entries keep their instance (cached pixel size; the shown tile stays bound to it).
                var byPath = new Dictionary<string, ExplorerEntry>(known.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in known) byPath.TryAdd(entry.Path, entry);
                for (var i = 0; i < listed.Count; i++)
                    if (byPath.TryGetValue(listed[i].Path, out var old) && ExplorerEntryList.SameListing(old, listed[i])) listed[i] = old;
                if (probeDimensions) ExplorerModel.ProbeForSort(listed, probeKey, token);
                return listed;
            }, token);
            if (version != Volatile.Read(ref _explorerInPlaceVersion) || generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState active || active.Id != activeTabId ||
                !string.Equals(active.Folder, folder, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_explorerLoadedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            ApplyExplorerListingInPlace(entries, pendingSelection);
            EnsureExplorerFolderWatch(folder);
        }
        catch (OperationCanceledException) { /* window closing */ }
        catch (UnauthorizedAccessException ex) { ShowExplorerMessage($"Can't open this folder: {ex.Message}", isError: true); }
        catch (IOException ex) when (!Directory.Exists(folder))
        {
            // The shown folder itself was deleted or renamed outside Glide: say so instead of showing stale tiles.
            ShowExplorerMessage($"This folder is no longer available: {ex.Message}", isError: true);
        }
        catch (IOException ex) { ShowExplorerMessage($"Can't read this folder: {ex.Message}", isError: true); }
    }

    /// <summary>UI thread: turns the shown list into the new listing with single removals / insertions.</summary>
    private void ApplyExplorerListingInPlace(List<ExplorerEntry> entries, List<string>? pendingSelection)
    {
        var currentIndex = _browserCurrentIndex;
        var currentPath = currentIndex >= 0 && currentIndex < _browserEntries.Count ? _browserEntries[currentIndex].Path : null;
        _explorerAllEntries = entries;
        var target = ComputeExplorerDisplayList();
        var diff = ListingDiff.Compute(_browserEntries, target, e => e.Path, ExplorerEntryList.SameListing);
        if (diff.OperationCount > ExplorerInPlaceMaxSteps)
        {
            _browserEntries = new ExplorerEntryList(target);
            BrowserRepeater.ItemsSource = _browserEntries;
        }
        else if (!diff.IsEmpty)
        {
            _explorerParkedThumbnails ??= new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            diff.ApplyTo(_browserEntries);
            _ = ReleaseParkedExplorerThumbnailsAsync();
        }

        var listed = new HashSet<string>(_browserEntries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        if (pendingSelection is not null)
        {
            _browserSelection.Clear();
            foreach (var path in pendingSelection)
                if (listed.Contains(path)) _browserSelection.Add(path);
        }
        else
        {
            _browserSelection.RemoveWhere(p => !listed.Contains(p));
        }
        _explorerCutPaths.RemoveWhere(p => !listed.Contains(p));
        _browserCurrentIndex = currentPath is null ? -1 : _browserEntries.FindIndex(e => string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        if (pendingSelection is not null && _browserSelection.Count > 0)
            _browserCurrentIndex = _browserEntries.FindIndex(e => _browserSelection.Contains(e.Path));
        else if (_browserCurrentIndex < 0 && currentPath is not null && _browserEntries.Count > 0)
            // The focused item went away: the focus moves to the item now in its place, like Explorer.
            _browserCurrentIndex = Math.Min(currentIndex, _browserEntries.Count - 1);
        _browserSelectionAnchor = _browserCurrentIndex;
        foreach (var tile in RealizedExplorerTiles()) UpdateExplorerTileVisual(tile);
        UpdateExplorerStatus();
        RefreshTagSidebarAfterListing();

        if (_explorerPendingRename is { } renamePath)
        {
            _explorerPendingRename = null;
            var index = _browserEntries.FindIndex(e => string.Equals(e.Path, renamePath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                SelectBrowserIndex(index, extend: false, toggle: false, bringIntoView: true);
                BeginExplorerRename(index);
            }
        }
        else if (pendingSelection is not null && _browserCurrentIndex >= 0)
        {
            EnsureExplorerIndexVisible(_browserCurrentIndex);
        }
        _diagnostics.Write("browser", "explorer_in_place_update", new
        {
            removed = diff.RemoveAt.Count, inserted = diff.Inserts.Count, swapped = diff.OperationCount > ExplorerInPlaceMaxSteps, count = _browserEntries.Count
        });
    }

    /// <summary>Watches the shown Explorer folder for outside changes (one watcher; retargeted when the shown folder changes).</summary>
    private void EnsureExplorerFolderWatch(string folder)
    {
        try
        {
            _explorerFolderWatch ??= new FolderWatchService((watched, _) =>
                Dispatcher.UIThread.Post(() => OnExplorerFolderChanged(watched), DispatcherPriority.Background),
                TimeSpan.FromMilliseconds(200), explorer: true);
            _explorerFolderWatch.Watch(folder);
        }
        catch (Exception ex)
        {
            _diagnostics.Write("browser", "explorer_watch_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    /// <summary>
    /// UI thread: the watched folder changed (added / removed / renamed entries, or a watcher overflow). Every case
    /// re-reads the folder and applies the difference in place, so an overflow is a full but flicker-free rescan, and
    /// Glide's own operations that the watcher also reports end up as an empty difference.
    /// </summary>
    private void OnExplorerFolderChanged(string folder)
    {
        if (_windowLifetimeCts.IsCancellationRequested || _explorerListing) return;
        if (_workspace.Active is not BrowserTabState active || !string.Equals(active.Folder, folder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_explorerLoadedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        // Glide's own operation is still running (it reloads when done) or a rename editor is open (it would lose the edit).
        if (_explorerOperationBusy || _explorerRenamePath is not null)
        {
            _ = RetryExplorerFolderChangeAsync(folder);
            return;
        }
        RefreshManagedBrowser(folder);
    }

    private async Task RetryExplorerFolderChangeAsync(string folder)
    {
        try { await Task.Delay(500, _windowLifetimeCts.Token); }
        catch (OperationCanceledException) { return; }
        OnExplorerFolderChanged(folder);
    }
}
