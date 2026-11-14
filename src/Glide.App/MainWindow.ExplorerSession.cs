using Avalonia.Threading;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Continuous "Open last session" persistence for Explorer tabs (4.8.0). A navigation, selection or
/// scroll change schedules one debounced snapshot of the session file, so a crash loses at most about
/// a second of Explorer state. Image-tab and close-time saving is unchanged.
/// </summary>
public partial class MainWindow
{
    /// <summary>Continuous scrolling cannot postpone the write for longer than this.</summary>
    private static readonly TimeSpan ExplorerSessionSaveMaxWait = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _explorerSessionSaveTimer;
    private long _explorerSessionPendingSinceTicks;
    private string? _explorerSessionDirectory;

    private bool ExplorerSessionAutosaveEnabled =>
        string.Equals(_settings.StartupAction, "Open last session", StringComparison.OrdinalIgnoreCase) &&
        IsVisible && !_speedBoostStandbyActive && !_closeForSpeedBoostStandby && !_forceProcessExit;

    /// <summary>An Explorer tab navigated, changed its selection or scrolled: (re)start the debounce.</summary>
    private void NoteExplorerSessionChanged()
    {
        if (_workspace.Active is not BrowserTabState || !ExplorerSessionAutosaveEnabled) return;
        var timer = _explorerSessionSaveTimer;
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += (_, _) => FlushExplorerSessionSave();
            _explorerSessionSaveTimer = timer;
        }
        var now = Environment.TickCount64;
        if (!timer.IsEnabled)
        {
            _explorerSessionPendingSinceTicks = now;
            // The file this burst of changes belongs to; the tick itself may run in another context.
            _explorerSessionDirectory = SettingsStore.GetSettingsDirectory();
        }
        else if (now - _explorerSessionPendingSinceTicks >= ExplorerSessionSaveMaxWait.TotalMilliseconds) return; // already due
        else timer.Stop();
        timer.Interval = WorkspaceSessionStore.AutosaveDelay;
        timer.Start();
    }

    /// <summary>Drops a pending debounced write (the window is closing or entering standby and saves itself).</summary>
    private void CancelPendingExplorerSessionSave() => _explorerSessionSaveTimer?.Stop();

    private void FlushExplorerSessionSave()
    {
        _explorerSessionSaveTimer?.Stop();
        if (!ExplorerSessionAutosaveEnabled || _workspace.Tabs.Count == 0) return;
        try
        {
            foreach (var tab in _workspace.Tabs.OfType<BrowserTabState>().ToArray()) CaptureTabRuntimeState(tab.Id);
            // Serialize here, on the UI thread, then write on a pool thread: a slow disk or virus
            // scanner never stalls the Explorer.
            var snapshot = WorkspaceSessionStore.Snapshot(_workspace.Tabs, _workspace.ActiveIndex, _explorerSessionDirectory);
            if (snapshot is null) return;
            _ = Task.Run(() =>
            {
                try { WorkspaceSessionStore.Write(snapshot); }
                catch { /* best effort, like every session write */ }
            });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("browser", "explorer_session_save_failed", new { error = ex.GetType().Name, ex.Message });
        }
    }

    /// <summary>
    /// The Explorer tab's selection and scroll as they are now, or null when they cannot be read yet
    /// (the folder is still loading), in which case the tab keeps what it already remembers.
    /// </summary>
    private BrowserTabViewState? CaptureExplorerViewFor(BrowserTabState browser)
    {
        if (_explorerDisplayedTabId == browser.Id)
        {
            if (_browserSurface is null || !string.Equals(_explorerLoadedFolder, browser.Folder, StringComparison.OrdinalIgnoreCase)) return null;
            var current = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count ? _browserEntries[_browserCurrentIndex].Path : null;
            var scroll = _explorerPendingScrollY ?? _browserSurface.ScrollViewer.Offset.Y;
            return new BrowserTabViewState(PickExplorerSessionSelection(_browserSelection, current, browser.Folder), Math.Max(0, scroll));
        }
        if (_explorerTabViews.TryGetValue(browser.Id, out var view) && string.Equals(view.Folder, browser.Folder, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserTabViewState(PickExplorerSessionSelection(view.Selection, null, browser.Folder), Math.Max(0, view.ScrollY));
        }
        return null;
    }

    /// <summary>One item to reselect later: the current one if it is selected, else any selected item of this folder.</summary>
    private static string? PickExplorerSessionSelection(IEnumerable<string> selection, string? current, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(folder);
        bool InFolder(string path) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""), parent, StringComparison.OrdinalIgnoreCase);
        var selected = selection as ICollection<string> ?? selection.ToList();
        if (current is not null && selected.Contains(current, StringComparer.OrdinalIgnoreCase) && InFolder(current)) return current;
        return selected.FirstOrDefault(InFolder);
    }

    /// <summary>
    /// The folder an Explorer tab should open: its own folder, else the nearest parent that exists
    /// (and, for a tab that has not been shown yet, can be read), else the usual start folder.
    /// </summary>
    private string ResolveExplorerRestoreFolder(string folder, bool checkReadable)
    {
        try
        {
            for (var current = Path.GetFullPath(folder); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current) ?? "")
            {
                if (!Directory.Exists(current)) continue;
                if (!checkReadable || IsReadableDirectory(current)) return current;
            }
        }
        catch { /* an unusable path falls through to the start folder */ }
        return ResolveFreshExplorerFolder();
    }

    private static bool IsReadableDirectory(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch { return false; }
    }
}
