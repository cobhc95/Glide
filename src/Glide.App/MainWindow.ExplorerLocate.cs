using Avalonia.Controls;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// The title-bar "Show in Explorer" button (id file.locate, command OpenContainingFolder): reveal the
/// current image either in Glide's own Explorer tab (default, 4.8.0) or in Windows File Explorer.
/// The choice is the <c>ShowInExplorerTarget</c> setting and can be flipped from the button's context menu.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Launches Windows File Explorer with the file selected; returns whether it was started. This is a
    /// seam so tests never start explorer.exe: replace it, run the command, then restore the original.
    /// </summary>
    internal static Func<string, bool> WindowsExplorerLocateLauncher { get; set; } =
        path => new FileOperationController().TryOpenContainingFolder(path);

    private bool LocateOpensWindowsExplorer => ShowInExplorerTargets.IsWindowsExplorer(_settings.ShowInExplorerTarget);

    /// <summary>Title-bar / tooltip label for a button; the locate button's text follows the current choice.</summary>
    private string TitleActionLabel(string id, TitleBarButtonDefinition definition) =>
        id == "file.locate" ? TitleBarButtonCatalog.LocateLabel(_settings.ShowInExplorerTarget) : definition.Label;

    /// <summary>The image the locate command applies to: the active image tab's file, never a stale path.</summary>
    private string? CurrentLocatePath()
    {
        if (_workspace.Active is not ImageTabState image) return null;
        if (!string.IsNullOrWhiteSpace(_currentPath) && File.Exists(_currentPath)) return _currentPath;
        return File.Exists(image.Path) ? image.Path : null;
    }

    private async Task OpenContainingFolderAsync()
    {
        var path = CurrentLocatePath();
        if (LocateOpensWindowsExplorer)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var opened = WindowsExplorerLocateLauncher(path);
            _diagnostics.Write("file", opened ? "open_containing_folder_external" : "open_containing_folder_failed", new { path });
            return;
        }
        await LocateInExplorerAsync(path);
    }

    /// <summary>Image view (zoom / pan) of a tab that turned into the Explorer view (Same tab), restored by Back.</summary>
    private readonly Dictionary<Guid, (string Path, ImageTabViewState View)> _locateReturnViews = new();

    /// <summary>
    /// Ctrl+E / Show in Explorer tab / Open file location: shows the picture's folder where the
    /// <c>explorer.locateOpensIn</c> setting says (same tab, existing or new tab, always a new tab, new window).
    /// </summary>
    private Task LocateInExplorerAsync(string? path)
    {
        var mode = ExplorerLocateTargets.Normalize(_settings.ExplorerLocateOpensIn);
        return mode switch
        {
            ExplorerLocateTargets.SameTab => ShowPathInSameTabAsync(path),
            ExplorerLocateTargets.NewTab => ShowPathInExplorerTabCoreAsync(path, alwaysNewTab: true),
            ExplorerLocateTargets.NewWindow => ShowPathInNewWindowAsync(path),
            _ => ShowPathInExplorerTabAsync(path)
        };
    }

    /// <summary>The file to select and its folder; both null when the path is unusable.</summary>
    private static (string? File, string? Folder) ResolveLocateTarget(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return (full, Path.GetDirectoryName(full));
            if (Directory.Exists(full)) return (null, full);
        }
        catch { /* unusable path */ }
        return (null, null);
    }

    /// <summary>
    /// Same tab: the active tab becomes the Explorer view of the folder with the file selected, as an ordinary push onto
    /// the tab's own history (Back returns to the image in this tab, Forward to the folder). An Explorer tab just navigates.
    /// </summary>
    private async Task ShowPathInSameTabAsync(string? path)
    {
        if (_workspace.Active is not { } active) { await ShowPathInExplorerTabAsync(path); return; }
        var (file, folder) = ResolveLocateTarget(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            if (active is BrowserTabState) return;
            file = null;
            folder = ResolveFreshExplorerFolder();
        }
        if (active is BrowserTabState browser)
        {
            if (!NavigationEntry.PathsEqual(browser.Folder, folder)) NavigateBrowserTo(folder, addHistory: true, highlightPath: file);
            _diagnostics.Write("file", "open_containing_folder_same_tab", new { path = file ?? folder, folder, explorer = true });
            return;
        }
        var history = HistoryFor(active);
        SyncCurrentHistoryEntry(active);
        if (active is ImageTabState image)
        {
            var shown = LiveImagePath(image);
            if (ImageView.IsVisible && NavigationEntry.PathsEqual(_currentPath, shown))
            {
                var view = Viewport.CaptureViewState();
                _locateReturnViews[active.Id] = (shown, new ImageTabViewState(view.Mode, view.Zoom, view.Pan.X, view.Pan.Y));
            }
        }
        if (file is not null) _browserHighlightTargets[active.Id] = file;
        else _browserHighlightTargets.Remove(active.Id);
        _workspace.ReplaceTab(new BrowserTabState(active.Id, folder) { View = new BrowserTabViewState(file, 0) });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        if (_workspace.Active?.Id == active.Id)
        {
            RecordActiveTabLocation(NavigationEntry.Explorer(folder));
            if (file is not null && history.Current is { Kind: NavigationEntryKind.Explorer }) history.UpdateCurrent(e => e with { SelectedPath = file });
            UpdateTabNavigationButtons();
        }
        _diagnostics.Write("file", "open_containing_folder_same_tab", new { path = file ?? folder, folder, explorer = false });
    }

    /// <summary>
    /// New window: a new Glide window with an Explorer tab of the folder, the file selected. Back there returns to the
    /// picture in this window (found through the window registry); when that tab is gone the picture reopens there.
    /// </summary>
    private async Task ShowPathInNewWindowAsync(string? path)
    {
        CrossTabLink? origin = _workspace.Active is ImageTabState fromImage
            ? new CrossTabLink(fromImage.Id, NavigationEntryKind.Image, LiveImagePath(fromImage))
            : null;
        var (file, folder) = ResolveLocateTarget(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { file = null; folder = ResolveFreshExplorerFolder(); }
        var tab = new BrowserTabState(Guid.NewGuid(), folder) { View = new BrowserTabViewState(file, 0) };
        var child = new MainWindow(tab, _settings.CloneState(), deferWorkspaceActivation: true);
        if (file is not null) child._browserHighlightTargets[tab.Id] = file;
        CopyWindowGeometryTo(child);
        ShowSecondaryWindow(child);
        await child.ActivateWorkspaceAsync();
        if (origin is not null && child._workspace.Active is BrowserTabState arrived)
        {
            child.RecordActiveTabLocation();
            CrossTabHistory.MarkArrival(child.HistoryFor(arrived), origin);
            child.UpdateTabNavigationButtons();
        }
        child.Activate();
        LastLocateWindowForTests = child;
        _diagnostics.Write("file", "open_containing_folder_new_window", new { path = file ?? folder, folder });
    }

    /// <summary>The window the last "New window" locate opened (tests).</summary>
    internal MainWindow? LastLocateWindowForTests { get; private set; }

    /// <summary>Back from a locate window: another Glide window that still has the image tab, activated on it.</summary>
    private async Task<bool> TryReturnToImageInOtherWindowAsync(CrossTabLink link)
    {
        foreach (var other in Glide.App.Platform.GlideWindowRegistry.Snapshot())
        {
            if (ReferenceEquals(other, this) || other._workspace.Tabs.FirstOrDefault(t => t.Id == link.TabId) is not ImageTabState) continue;
            if (other._workspace.Active?.Id != link.TabId) { other._workspace.Select(link.TabId); other.RebuildTabStrip(); await other.ActivateWorkspaceAsync(); }
            other.Activate();
            _diagnostics.Write("navigation", "history_cross_window", new { path = link.Path });
            return true;
        }
        return false;
    }

    /// <summary>
    /// Shows <paramref name="path"/> (a file, or a folder) in an Explorer tab of this window with the file
    /// selected and scrolled into view. Reuses an Explorer tab (the active one first) that already shows that folder,
    /// otherwise opens a new Explorer tab. With no usable path it opens the Explorer tab at the usual
    /// fresh-tab location (last location / Pictures) instead of failing silently.
    /// </summary>
    private Task ShowPathInExplorerTabAsync(string? path) => ShowPathInExplorerTabCoreAsync(path, alwaysNewTab: false);

    /// <summary>As above; <paramref name="alwaysNewTab"/> skips reusing a tab (the "Always a new tab" choice).</summary>
    private async Task ShowPathInExplorerTabCoreAsync(string? path, bool alwaysNewTab)
    {
        // Back from the Explorer tab returns to this image (CrossTabHistory): remember where the jump came from.
        CrossTabLink? origin = _workspace.Active is ImageTabState fromImage
            ? new CrossTabLink(fromImage.Id, NavigationEntryKind.Image, LiveImagePath(fromImage))
            : null;
        string? file = null;
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full)) { file = full; folder = Path.GetDirectoryName(full); }
                else if (Directory.Exists(full)) folder = full;
            }
            catch { /* unusable path: file and folder stay null and the caller reports it */ }
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            file = null;
            // Nothing to select. When an Explorer tab is already in front there is nothing more to do.
            if (_workspace.Active is BrowserTabState) return;
            folder = ResolveFreshExplorerFolder();
        }

        static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        Guid tabId;
        bool SameFolder(TabState tab) => tab is BrowserTabState b &&
            string.Equals(Normalize(b.Folder), Normalize(folder), StringComparison.OrdinalIgnoreCase);
        // Reuse an Explorer tab already showing that folder: the active one first, then any other tab of this window.
        var existing = alwaysNewTab ? null : _workspace.Active is { } current && SameFolder(current) ? current : _workspace.Tabs.FirstOrDefault(SameFolder);
        var reuse = existing is not null;
        if (existing is not null)
        {
            tabId = existing.Id;
            if (_workspace.Active?.Id != tabId) { _workspace.Select(tabId); RebuildTabStrip(); }
        }
        else
        {
            tabId = _workspace.AddBrowser(folder).Id;
            RebuildTabStrip();
        }
        // Contract with the Explorer surface: once the tab is active it selects and scrolls to this path.
        if (file is not null) _browserHighlightTargets[tabId] = file;
        else _browserHighlightTargets.Remove(tabId);
        await ActivateWorkspaceAsync();
        if (origin is not null && _workspace.Active is BrowserTabState arrived && arrived.Id == tabId)
        {
            RecordActiveTabLocation();
            CrossTabHistory.MarkArrival(HistoryFor(arrived), origin);
            UpdateTabNavigationButtons();
        }
        _diagnostics.Write("file", "open_containing_folder_explorer_tab", new { path = file ?? folder, folder, reused = reuse });
    }

    private MenuItem BuildShowInExplorerTargetMenuItem(string text, string target, string icon)
    {
        var selected = string.Equals(ShowInExplorerTargets.Normalize(_settings.ShowInExplorerTarget), target, StringComparison.Ordinal);
        var item = Glide.App.Services.MenuActions.Tag(new MenuItem { Header = selected ? $"{text}  ✓" : text, Icon = MenuIcon(icon) }, "setting.window.showInExplorerTarget");
        item.Click += (_, _) => SetShowInExplorerTarget(target);
        return item;
    }

    private void SetShowInExplorerTarget(string target)
    {
        _settings.ShowInExplorerTarget = ShowInExplorerTargets.Normalize(target);
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }
}
