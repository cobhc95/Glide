using Avalonia.Controls;
using Glide.Core;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Browser-style Back / Forward per tab (5.0.0, docs/NAVIGATION-HISTORY.md). Every tab owns one
/// <see cref="NavigationHistory"/> (Glide.Core): Explorer folders, an image opened from the Explorer or from Home in the
/// same tab, and Home are history entries. Image-to-image navigation (arrows, wheel, slideshow, sibling folders, Open in
/// the same tab) replaces the current image entry instead of adding steps. The Explorer tab's own Back / Forward buttons,
/// the history dropdown, Alt+Left / Alt+Right, the title-bar Back / Forward buttons and the mouse side buttons all use
/// this one history.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<Guid, NavigationHistory> _tabHistories = new();
    private bool _historyResolvedToParent;
    /// <summary>The last short message Back / Forward showed (a skipped image, a folder that fell back to its parent).</summary>
    internal string? LastHistoryNotice { get; private set; }

    /// <summary>The history of <paramref name="tab"/>, created on first use: from the copy that travelled with the tab
    /// (drag to another window), from a restored Explorer tab's folder history (session file), or empty.</summary>
    internal NavigationHistory HistoryFor(TabState tab)
    {
        if (_tabHistories.TryGetValue(tab.Id, out var history)) return history;
        history = tab.History is { } travelled ? NavigationHistory.FromState(travelled)
            : tab is BrowserTabState { Navigation: { } navigation } browser ? FromBrowserNavigation(browser, navigation)
            : new NavigationHistory();
        _tabHistories[tab.Id] = history;
        return history;
    }

    /// <summary>The active tab's history, or null when it has none yet (no Back / Forward).</summary>
    internal NavigationHistory? ActiveHistory =>
        _workspace.Active is { } active && _tabHistories.TryGetValue(active.Id, out var history) ? history : null;

    /// <summary>
    /// A restored Explorer tab (session file, older Glide) only knows its folders: they become Explorer entries
    /// (folders that are gone are dropped) with their remembered highlight as the selection. A restored view of the
    /// current folder is authoritative, so its older highlight is not used.
    /// </summary>
    private static NavigationHistory FromBrowserNavigation(BrowserTabState browser, BrowserNavigationState navigation)
    {
        var entries = new List<NavigationEntry>();
        var index = -1;
        for (var i = 0; i < navigation.History.Count; i++)
        {
            var folder = navigation.History[i];
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                var highlight = navigation.Highlights is { } highlights && i < highlights.Count && !string.IsNullOrWhiteSpace(highlights[i]) ? highlights[i] : null;
                if (i <= navigation.Index || index < 0) index = entries.Count;
                entries.Add(NavigationEntry.Explorer(Path.GetFullPath(folder), highlight));
            }
            catch { /* a damaged entry only loses that step */ }
        }
        if (entries.Count == 0) return new NavigationHistory();
        index = Math.Clamp(index, 0, entries.Count - 1);
        if (browser.View is not null) entries[index] = entries[index] with { SelectedPath = null };
        return NavigationHistory.FromState(new NavigationHistoryState(entries, index));
    }

    /// <summary>The Explorer folder history of a tab for the session file (its Explorer entries, newest last).</summary>
    private BrowserNavigationState? BrowserNavigationFor(Guid tabId)
    {
        if (!_tabHistories.TryGetValue(tabId, out var history) || history.Count == 0) return null;
        var folders = new List<string>();
        var highlights = new List<string?>();
        var index = -1;
        for (var i = 0; i < history.Entries.Count; i++)
        {
            var entry = history.Entries[i];
            if (entry.Kind != NavigationEntryKind.Explorer || entry.Path is null) continue;
            if (i <= history.Index) index = folders.Count;
            folders.Add(entry.Path);
            highlights.Add(entry.SelectedPath);
        }
        return folders.Count == 0 ? null : new BrowserNavigationState(folders, Math.Max(0, index), highlights);
    }

    /// <summary>What the tab shows now, as a history entry.</summary>
    private NavigationEntry? LiveEntryFor(TabState tab) => tab switch
    {
        HomeTabState => NavigationEntry.Home(),
        ImageTabState image => NavigationEntry.Image(LiveImagePath(image)),
        BrowserTabState browser => NavigationEntry.Explorer(browser.Folder),
        _ => null
    };

    /// <summary>The image on screen for the active image tab (arrow navigation may be ahead of the tab record).</summary>
    private string LiveImagePath(ImageTabState image)
    {
        if (_workspace.Active?.Id == image.Id && ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath))
        {
            try { return Path.GetFullPath(_currentPath); } catch { /* invalid path: fall through to the raw value */ }
        }
        return image.Path;
    }

    /// <summary>
    /// The active tab now shows what its state says (or <paramref name="entry"/>, when the caller knows better, for
    /// example an image that is about to load): record it (first entry, a new step, or image-to-image replace).
    /// </summary>
    private void RecordActiveTabLocation(NavigationEntry? entry = null)
    {
        if (_workspace.Active is not { } active || (entry ?? LiveEntryFor(active)) is not { } shown) return;
        HistoryFor(active).Record(shown);
        PruneTabHistories();
        UpdateHistoryNavigationState();
    }

    /// <summary>Starts the tab's history over at what it shows now (a placeholder Home replaced by a launched file).</summary>
    private void ResetActiveTabHistory(NavigationEntry? entry = null)
    {
        if (_workspace.Active is not { } active) return;
        _tabHistories[active.Id] = new NavigationHistory();
        RecordActiveTabLocation(entry);
    }

    /// <summary>
    /// Removes a tab that moves to another Glide window (drag / detach). Its history travels with it in
    /// <see cref="TabState.History"/>, so Back / Forward keep working in the new window.
    /// </summary>
    private TabState? RemoveTabForTransfer(Guid id)
    {
        var removed = _workspace.RemoveForTransfer(id);
        if (removed is null) return null;
        if (_tabHistories.Remove(id, out var history) && history.Count > 0) removed = removed with { History = history.Snapshot() };
        return removed;
    }

    private void PruneTabHistories()
    {
        if (_tabHistories.Count <= _workspace.Tabs.Count) return;
        var live = _workspace.Tabs.Select(t => t.Id).ToHashSet();
        foreach (var id in _tabHistories.Keys.Where(id => !live.Contains(id)).ToArray()) _tabHistories.Remove(id);
    }

    /// <summary>
    /// Before the tab leaves its current entry: remember in it what is on screen now. Image: the image actually shown
    /// (Back from an image opened from the Explorer then selects THAT image, Forward returns to it). Explorer: the
    /// selected item, the scroll offset and the view mode.
    /// </summary>
    private void SyncCurrentHistoryEntry(TabState tab)
    {
        var history = HistoryFor(tab);
        if (history.Current is not { } current)
        {
            if (LiveEntryFor(tab) is { } first) history.Record(first);
            return;
        }
        switch (tab)
        {
            case ImageTabState image when current.Kind == NavigationEntryKind.Image:
                var shown = NavigationEntry.Image(LiveImagePath(image));
                if (!current.SameLocation(shown)) history.Replace(shown);
                break;
            case BrowserTabState browser:
                CaptureExplorerHistoryEntry(history, browser);
                break;
        }
    }

    /// <summary>The Explorer selection / scroll / view mode of the folder on screen into the current Explorer entry.</summary>
    private void CaptureExplorerHistoryEntry(NavigationHistory history, BrowserTabState browser)
    {
        if (history.Current is not { Kind: NavigationEntryKind.Explorer } current || !NavigationEntry.PathsEqual(current.Path, browser.Folder)) return;
        if (_explorerDisplayedTabId != browser.Id || _browserSurface is not { } surface || !NavigationEntry.PathsEqual(_explorerLoadedFolder, browser.Folder)) return;
        var selected = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count
            ? _browserEntries[_browserCurrentIndex].Path
            : _browserSelection.FirstOrDefault();
        var scroll = _explorerPendingScrollY ?? surface.ScrollViewer.Offset.Y;
        history.UpdateCurrent(entry => entry with
        {
            SelectedPath = selected ?? entry.SelectedPath,
            ScrollY = double.IsFinite(scroll) && scroll > 0 ? scroll : 0,
            ViewMode = _browserViewMode
        });
    }

    internal Task NavigateBackAsync() => NavigateHistoryAsync(-1);
    internal Task NavigateForwardAsync() => NavigateHistoryAsync(1);

    /// <summary>
    /// Backspace (nav.backOrPrevious): Back when this tab's history has a step back; otherwise, in an image tab, the previous
    /// image (an image opened directly from Windows Explorer or in a fresh tab has no history); otherwise nothing (an Explorer
    /// or Home tab without history). Alt+Left / the mouse back button are plain Back and never fall back.
    /// </summary>
    internal Task BackOrPreviousAsync()
    {
        if (CrossTabHistory.CanGo(ActiveHistory, -1, _settings.ExplorerBackReturnsToImage)) return NavigateBackAsync();
        if (_workspace.Active is ImageTabState && ImageView.IsVisible) return NavigateKeyboardAsync(-1);
        return Task.CompletedTask;
    }

    /// <summary>History dropdown: jump to one entry of the active tab's history.</summary>
    private Task NavigateHistoryToIndexAsync(int index) => NavigateHistoryAsync(0, index);

    private async Task NavigateHistoryAsync(int direction, int? targetIndex = null)
    {
        // Every history change happens synchronously before the first await, so steps never interleave. A step taken
        // while the previous image is still loading simply supersedes it (the workspace epoch cancels the old load,
        // exactly like switching tabs mid-load), like pressing Back again in a browser before the page finished.
        if (_workspace.Active is not { } active) return;
        try
        {
            var history = HistoryFor(active);
            SyncCurrentHistoryEntry(active);
            var leaving = history.Current;
            _historyResolvedToParent = false;
            NavigationEntry? target;
            var skipped = 0;
            if (targetIndex is { } index)
            {
                if (index == history.Index) return;
                var before = history.Count;
                target = history.GoTo(index, ResolveHistoryTarget);
                skipped = before - history.Count;
            }
            else
            {
                // A jump from an image to an Explorer tab (Ctrl+E ...) is one chronological step (CrossTabHistory).
                var decision = CrossTabHistory.Decide(history, direction, _settings.ExplorerBackReturnsToImage);
                if (decision is { Kind: CrossTabStepKind.ToOtherTab, Link: { } link })
                {
                    await CrossTabStepAsync(active, history, link, direction);
                    return;
                }
                target = history.Step(direction, ResolveHistoryTarget, out skipped);
            }
            var fellBack = _historyResolvedToParent;

            _diagnostics.Write("navigation", "history_step", new
            {
                direction, targetIndex, kind = target?.Kind.ToString(), path = target?.Path, index = history.Index, count = history.Count,
                skipped, fellBack
            });
            if (target is null)
            {
                if (skipped > 0) ShowHistoryNotice(skipped == 1 ? "That image no longer exists." : "Those images no longer exist.");
                return;
            }
            await ShowHistoryEntryAsync(active, history, leaving, target);
            if (fellBack) ShowHistoryNotice("That folder no longer exists; showing the nearest folder that does.");
            else if (skipped > 0) ShowHistoryNotice(skipped == 1 ? "Skipped an image that no longer exists." : $"Skipped {skipped} images that no longer exist.");
        }
        finally
        {
            UpdateHistoryNavigationState();
        }
    }

    /// <summary>
    /// Back from an Explorer tab to the image the user jumped from (its tab, or a new one when it was closed), or Forward
    /// from that image back to the Explorer tab. The Explorer history itself does not move, so Forward lands on the same
    /// folder and selection.
    /// </summary>
    private async Task CrossTabStepAsync(TabState active, NavigationHistory history, CrossTabLink link, int direction)
    {
        _diagnostics.Write("navigation", "history_cross_tab", new { direction, kind = link.Kind.ToString(), path = link.Path });
        if (direction < 0)
        {
            if (!File.Exists(link.Path)) { ShowHistoryNotice("That image no longer exists."); return; }
            var arrival = CrossTabHistory.ArrivalIndex(history, link);
            var explorerLink = new CrossTabLink(active.Id, NavigationEntryKind.Explorer,
                (active as BrowserTabState)?.Folder ?? history.Current?.Path ?? string.Empty, history.Current?.SelectedPath);
            Guid imageTabId;
            // The Explorer window opened by Ctrl+E (New window): Back goes to the picture in the original window.
            if (_workspace.Tabs.All(t => t.Id != link.TabId) && await TryReturnToImageInOtherWindowAsync(link)) return;
            if (_workspace.Tabs.FirstOrDefault(t => t.Id == link.TabId) is ImageTabState source)
            {
                imageTabId = source.Id;
                _workspace.Select(imageTabId);
                if (!NavigationEntry.PathsEqual(source.Path, link.Path)) _workspace.ReplaceTab(new ImageTabState(imageTabId, link.Path));
            }
            else
            {
                imageTabId = _workspace.AddImage(link.Path).Id;
                // Back from the Explorer tab again finds the reopened tab.
                if (arrival >= 0)
                {
                    var current = history.Index;
                    history.GoTo(arrival);
                    history.UpdateCurrent(e => e with { ReturnTo = link with { TabId = imageTabId } });
                    history.GoTo(current);
                }
            }
            RebuildTabStrip();
            var imageHistory = HistoryFor(_workspace.Active!);
            imageHistory.Record(NavigationEntry.Image(link.Path));
            if (!NavigationEntry.PathsEqual(imageHistory.Current?.Path, link.Path)) imageHistory.Replace(NavigationEntry.Image(link.Path));
            CrossTabHistory.MarkReturned(imageHistory, explorerLink);
            UpdateTabNavigationButtons();
            await ActivateWorkspaceAsync();
            // Again after the image tab recorded what it shows (same image keeps the link; this covers a path rewrite).
            if (_workspace.Active?.Id == imageTabId) CrossTabHistory.MarkReturned(HistoryFor(_workspace.Active), explorerLink);
            return;
        }
        if (_workspace.Tabs.FirstOrDefault(t => t.Id == link.TabId) is BrowserTabState explorer)
        {
            _workspace.Select(explorer.Id);
            RebuildTabStrip();
            await ActivateWorkspaceAsync();
            return;
        }
        // The Explorer tab was closed: open the folder again with the same item selected (a new arrival from this image).
        await ShowPathInExplorerTabAsync(link.SelectedPath is { } selected && (File.Exists(selected) || Directory.Exists(selected)) ? selected : link.Path);
    }

    private NavigationEntry? ResolveHistoryTarget(NavigationEntry entry)
    {
        var resolved = NavigationTargetResolver.Resolve(entry, path => File.Exists(path) && ImageNavigator.IsSupported(path), Directory.Exists);
        if (resolved is not null && entry.Kind == NavigationEntryKind.Explorer && !NavigationEntry.PathsEqual(resolved.Path, entry.Path))
            _historyResolvedToParent = true;
        return resolved;
    }

    /// <summary>Makes the active tab show <paramref name="target"/> (the history index already points at it).</summary>
    private async Task ShowHistoryEntryAsync(TabState active, NavigationHistory history, NavigationEntry? leaving, NavigationEntry target)
    {
        switch (target.Kind)
        {
            case NavigationEntryKind.Explorer when active is BrowserTabState:
                // Folder to folder inside the Explorer tab: the remembered selection and scroll come back.
                _explorerPendingScrollY = target.ScrollY;
                _explorerPendingEnsureVisiblePath = target.SelectedPath;
                NavigateBrowserTo(target.Path!, addHistory: false);
                FocusBrowserList();
                return;
            case NavigationEntryKind.Explorer:
            {
                var folder = target.Path!;
                // Back from an image opened from this folder selects the image that is on screen NOW (the user may have
                // moved on with the arrow keys), not the one that was opened.
                var selected = target.SelectedPath;
                if (leaving is { Kind: NavigationEntryKind.Image, Path: { } shown } &&
                    NavigationEntry.PathsEqual(Path.GetDirectoryName(shown), folder) && File.Exists(shown))
                    selected = shown;
                history.UpdateCurrent(entry => entry with { SelectedPath = selected });
                PrepareExplorerViewForHistory(active.Id, folder, selected, target);
                _workspace.ReplaceTab(new BrowserTabState(active.Id, folder) { View = new BrowserTabViewState(selected, target.ScrollY) });
                await ActivateWorkspaceAsync();
                return;
            }
            case NavigationEntryKind.Image:
                // Back after Ctrl+E in the same tab: the picture comes back at the zoom and position it was left at.
                var returnView = _locateReturnViews.Remove(active.Id, out var saved) && NavigationEntry.PathsEqual(saved.Path, target.Path) ? saved.View : null;
                _workspace.ReplaceTab(new ImageTabState(active.Id, target.Path!) { ViewState = returnView });
                RebuildTabStrip();
                await ActivateWorkspaceAsync();
                return;
            case NavigationEntryKind.Home:
                _workspace.ReplaceTab(new HomeTabState(active.Id));
                await ActivateWorkspaceAsync();
                return;
        }
    }

    /// <summary>
    /// The Explorer view the tab returns to: the tab's own last view of that folder (sort, filter and icon size as the
    /// user left them) or, for another folder, the tab's sort / icon size with a fresh filter. The entry's selection,
    /// scroll and view mode are applied on top, and the selected item is scrolled into view if the scroll misses it.
    /// </summary>
    private void PrepareExplorerViewForHistory(Guid tabId, string folder, string? selected, NavigationEntry entry)
    {
        _explorerTabViews.TryGetValue(tabId, out var previous);
        var view = previous is not null && NavigationEntry.PathsEqual(previous.Folder, folder)
            ? previous
            : new ExplorerTabView
            {
                Folder = folder,
                ViewMode = previous?.ViewMode ?? Glide.App.Explorer.ExplorerViewModes.IndexOf(_settings.ExplorerDefaultView),
                SortKey = previous?.SortKey ?? Glide.App.Explorer.ExplorerSortKey.Name,
                SortAscending = previous?.SortAscending ?? true,
                FoldersFirst = previous?.FoldersFirst ?? true
            };
        view.Folder = folder;
        if (entry.ViewMode is { } mode) view.ViewMode = mode;
        view.Selection = selected is null ? new List<string>() : new List<string> { selected };
        view.CurrentIndex = -1;
        view.AnchorIndex = -1;
        view.ScrollY = entry.ScrollY;
        _explorerTabViews[tabId] = view;
        _explorerPendingEnsureVisiblePath = selected;
    }

    /// <summary>Enables / dims every Back / Forward control for the active tab.</summary>
    private void UpdateHistoryNavigationState()
    {
        var history = ActiveHistory;
        var mode = _settings.ExplorerBackReturnsToImage;
        var canBack = CrossTabHistory.CanGo(history, -1, mode);
        var canForward = CrossTabHistory.CanGo(history, 1, mode);
        var backTip = HistoryTip(-1, CrossTabHistory.Decide(history, -1, mode));
        var forwardTip = HistoryTip(1, CrossTabHistory.Decide(history, 1, mode));
        // Title bar: dimmed and inert rather than IsEnabled = false. A disabled control is skipped by hit testing, so a click
        // on it would fall through to the caption (window drag, double-click maximize); an inactive button just does nothing.
        // Its tooltip keeps showing the live shortcut.
        if (_titleBarButtons.TryGetValue("nav.back", out var back)) { SetHistoryButtonAvailable(back, canBack); ToolTip.SetTip(back, backTip); }
        if (_titleBarButtons.TryGetValue("nav.forward", out var forward)) { SetHistoryButtonAvailable(forward, canForward); ToolTip.SetTip(forward, forwardTip); }
        if (_browserSurface is { } surface && _workspace.Active is BrowserTabState)
        {
            surface.BackButton.IsEnabled = canBack;
            surface.ForwardButton.IsEnabled = canForward;
            ToolTip.SetTip(surface.BackButton, backTip);
            ToolTip.SetTip(surface.ForwardButton, forwardTip);
            surface.HistoryButton.IsEnabled = history is { Count: > 1 };
        }
    }

    /// <summary>"Back to 4K Wallpapers 1 (1).jpg (Alt+Left)" when Back leaves the tab, else "Back (Alt+Left)"; live keys.</summary>
    private string HistoryTip(int direction, CrossTabStep step)
    {
        var text = step.Link is { } link ? CrossTabHistory.Describe(direction, link) : direction < 0 ? "Back" : "Forward";
        var command = direction < 0 ? Glide.Core.Commands.GlideCommand.NavigateBack : Glide.Core.Commands.GlideCommand.NavigateForward;
        return $"{text} ({ShortcutFor(command)})"; // same live-binding format as RefreshDynamicTooltips
    }

    private static void SetHistoryButtonAvailable(Button button, bool available)
    {
        button.Classes.Set("inactive", !available);
        button.Opacity = available ? 1.0 : 0.34;
    }

    private void ShowHistoryNotice(string message)
    {
        LastHistoryNotice = message;
        if (_workspace.Active is BrowserTabState) ShowExplorerMessage(message);
        else if (ImageView.IsVisible) ShowViewerNotice(message);
        _diagnostics.Write("navigation", "history_notice", new { message });
    }

    /// <summary>Label and icon of a history entry for the dropdown.</summary>
    private static (string Header, string Icon, string Tip) HistoryEntryLabel(NavigationEntry entry)
    {
        switch (entry.Kind)
        {
            case NavigationEntryKind.Home:
                return ("Home", "Home", "Welcome page");
            case NavigationEntryKind.Image:
                return (Path.GetFileName(entry.Path) ?? "Image", "Image", entry.Path ?? string.Empty);
            default:
                var path = entry.Path ?? string.Empty;
                var name = Path.GetFileName(path.TrimEnd('\\'));
                return (string.IsNullOrEmpty(name) ? path : name, "Folder", path);
        }
    }
}
