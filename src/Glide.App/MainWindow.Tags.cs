using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.App.Explorer;
using Glide.Core.Navigation;
using Glide.Core.Renaming;
using Glide.Core.Tags;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Issue #4: batch rename (Explorer, several items selected) and tags written into file names (the tag sidebar in
/// the Explorer tab and the picture view). The rules live in Glide.Core (Renaming, Tags); this file only connects them
/// to the window: dialog, sidebar, selection, and refreshing after the files were renamed.
/// </summary>
public partial class MainWindow
{
    private TagSidebar? _tagSidebar;
    /// <summary>
    /// The user closed the sidebar (its close button or the toggle action). With tags on, the sidebar shows by default in
    /// Explorer and picture tabs; this lasts until tags are switched off or the app restarts.
    /// </summary>
    private bool _tagSidebarDismissed;
    private bool _tagChangeBusy;
    /// <summary>The last batch the rename dialog did (from, to), for its "Undo last rename"; null after an undo. Lasts until the app closes.</summary>
    private IReadOnlyList<(string From, string To)>? _lastBatchRename;

    // ------------------------------------------------------------------------------------------
    // Batch rename
    // ------------------------------------------------------------------------------------------

    /// <summary>Opens the batch rename dialog for these Explorer items and, when it renamed them, reloads the folder keeping them selected.</summary>
    private async Task ExplorerBatchRenameAsync(IReadOnlyList<ExplorerEntry> entries)
    {
        if (entries.Count == 0 || ExplorerCurrentFolder is not { } folder) return;
        CommitOrCancelExplorerRename(commit: false);
        var sources = entries.Select(e => new BatchRenameSource(e.Path, e.IsDirectory)).ToList();
        var dialog = new BatchRenameDialog(sources, _settings.BatchRenamePresets, null, _lastBatchRename) { Icon = Icon };
        // "Saved:" presets are stored the moment they change, even when the dialog is cancelled afterwards.
        dialog.PresetsChanged += json =>
        {
            if (string.Equals(json, _settings.BatchRenamePresets, StringComparison.Ordinal)) return;
            _settings.BatchRenamePresets = json;
            SaveSettingsAndPublish();
        };
        _overlays?.SetModalSuppressed(true);
        BatchRenameDialogResult? result;
        try { result = await ShowOwnedDialogAsync<BatchRenameDialogResult?>(dialog); }
        finally
        {
            _overlays?.SetModalSuppressed(false);
            // The Rename click must not also act on the list behind the dialog (see BrowserItemPointerPressed).
            _explorerIgnorePressesUntilTick = Environment.TickCount64 + ExplorerInputGraceAfterDialogMs;
        }
        if (result is null) return;

        // One level of undo: the batch just done, or nothing after an undo.
        _lastBatchRename = result.IsUndo ? null : result.Renamed;
        var map = result.Renamed.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
        foreach (var from in map.Keys) _explorerCutPaths.Remove(from);
        FollowRenamedPictures(map);
        _explorerPendingSelection = result.IsUndo
            ? result.Renamed.Select(m => m.To).ToList()
            : entries.Select(e => map.TryGetValue(e.Path, out var moved) ? moved : e.Path).ToList();
        RefreshManagedBrowser(folder);
        ShowExplorerMessage(result.IsUndo ? $"Undid the last rename ({result.Renamed.Count} {(result.Renamed.Count == 1 ? "item" : "items")})"
            : result.Renamed.Count == 1 ? $"Renamed to '{Path.GetFileName(result.Renamed[0].To)}'" : $"Renamed {result.Renamed.Count} items");
        await _explorerRefreshTask;
    }

    /// <summary>
    /// Files were renamed from an Explorer tab. The picture this tab turned away from (Ctrl+E on a picture) and every history
    /// entry that points at a renamed file follow the new name, silently: nothing is shown, no tab changes kind and no Back /
    /// Forward step is added (5.1.7 desktop retest, C: the rename used to bring the viewer back over the Explorer list).
    /// </summary>
    private void FollowRenamedPictures(IReadOnlyDictionary<string, string> renamed)
    {
        if (renamed.Count == 0) return;
        _tabHistories.RetargetImages(renamed);
        foreach (var id in _locateReturnViews.Keys.ToArray())
        {
            var (path, view) = _locateReturnViews[id];
            foreach (var (from, to) in renamed)
                if (NavigationEntry.PathsEqual(path, from)) { _locateReturnViews[id] = (to, view); break; }
        }
        if (_workspace.Active is not ImageTabState && _currentPath is { } shown)
            foreach (var (from, to) in renamed)
                if (NavigationEntry.PathsEqual(shown, from)) { _currentPath = to; break; }
    }

    // ------------------------------------------------------------------------------------------
    // Tags
    // ------------------------------------------------------------------------------------------

    private TagOptions CurrentTagOptions => TagOptions.From(_settings);

    /// <summary>The "Show / hide the tag sidebar" action.</summary>
    private Task ToggleTagSidebarAsync()
    {
        if (!_settings.TagsEnabled)
        {
            ShowTagMessage("Tags are off. Turn on Enable tags in Settings > Window & navigation > Tags.", isError: true);
            return Task.CompletedTask;
        }
        // Toggle what is on screen: showing now means hide, hidden (or not applicable yet) means show.
        _tagSidebarDismissed = TagSidebarHost.IsVisible;
        UpdateTagSidebar();
        return Task.CompletedTask;
    }

    /// <summary>The files the sidebar would tag now: the selected Explorer files, or the picture on screen.</summary>
    private List<string> TagTargetPaths()
    {
        if (_workspace.Active is BrowserTabState && BrowserView.IsVisible)
            return SelectedExplorerEntries().Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
        if (_workspace.Active is ImageTabState && ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath))
            return [_currentPath];
        return [];
    }

    /// <summary>Shows or hides the sidebar for the active tab, makes room for it and points it at the current files.</summary>
    private void UpdateTagSidebar()
    {
        var onFiles = (_workspace.Active is BrowserTabState && BrowserView.IsVisible) || (_workspace.Active is ImageTabState && ImageView.IsVisible);
        // Tags off forgets a dismissal, so switching them on again brings the sidebar back.
        if (!_settings.TagsEnabled) _tagSidebarDismissed = false;
        var show = _settings.TagsEnabled && !_tagSidebarDismissed && onFiles && !_wholeAppOverlayMode;
        if (!show)
        {
            TagSidebarHost.IsVisible = false;
            ImageView.Margin = default;
            BrowserView.Margin = default;
            return;
        }
        if (_tagSidebar is null)
        {
            _tagSidebar = new TagSidebar();
            _tagSidebar.ChangeRequested += (add, remove) => _ = ApplyTagChangeAsync(add, remove);
            _tagSidebar.CloseRequested += () => { _tagSidebarDismissed = true; UpdateTagSidebar(); };
            _tagSidebar.SettingsRequested += () => _ = OpenTagSettingsAsync();
            _tagSidebar.ApplyImmediatelyChanged += immediate =>
            {
                if (_settings.TagsApplyImmediately == immediate) return;
                _settings.TagsApplyImmediately = immediate;
                SaveSettingsAndPublish();
            };
            TagSidebarHost.Child = _tagSidebar;
        }
        var margin = new Thickness(0, 0, TagSidebar.PanelWidth, 0);
        ImageView.Margin = ImageView.IsVisible ? margin : default;
        BrowserView.Margin = BrowserView.IsVisible ? margin : default;
        TagSidebarHost.IsVisible = true;
        _tagSidebar.Configure(CurrentTagOptions, _settings.TagsApplyImmediately);
        RefreshTagSidebarTargets();
    }

    /// <summary>The selection or picture changed: the ticks follow the names of the files now selected.</summary>
    private void RefreshTagSidebarTargets()
    {
        if (_tagSidebar is null || !TagSidebarHost.IsVisible) return;
        var paths = TagTargetPaths();
        var names = paths.Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
        var caption = names.Count switch { 0 => "", 1 => names[0], _ => $"{names.Count} files selected" };
        _tagSidebar.SetTargets(names, caption);
    }

    /// <summary>
    /// The Explorer list was (re)built or re-sorted: the sidebar names the selection it now has (an Explorer tab shown with an item
    /// already selected, a restored session), not the empty list it was configured with while the folder was still loading.
    /// Not while a tag change is renaming: the sidebar then shows the names the queued clicks will produce.
    /// </summary>
    private void RefreshTagSidebarAfterListing()
    {
        if (!_tagChangeBusy) RefreshTagSidebarTargets();
    }

    private readonly Queue<(IReadOnlyList<string> Add, IReadOnlyList<string> Remove)> _tagChangeQueue = new();
    private List<string>? _tagPredictedNames;

    private List<string> PredictTagNames(IEnumerable<string> names, IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var options = CurrentTagOptions;
        return names.Select(n => TagFileName.Edit(n, add, remove, options)).ToList();
    }

    /// <summary>
    /// A tag click. A click that arrives while an earlier change is still renaming (or the picture is reloading) is queued and
    /// applied right after it, in order, to the files as they are named by then; meanwhile the sidebar's ticks show what the names
    /// will be once everything queued is applied, so the next click toggles the pending state.
    /// </summary>
    private async Task ApplyTagChangeAsync(IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        if (_tagChangeBusy)
        {
            if (add.Count == 0 && remove.Count == 0) return;
            _tagChangeQueue.Enqueue((add, remove));
            if (_tagPredictedNames is { } basis && _tagSidebar is not null)
            {
                _tagPredictedNames = PredictTagNames(basis, add, remove);
                var caption = _tagPredictedNames.Count switch { 0 => "", 1 => _tagPredictedNames[0], _ => $"{_tagPredictedNames.Count} files selected" };
                _tagSidebar.SetTargets(_tagPredictedNames, caption);
            }
            return;
        }
        var initial = TagTargetPaths();
        if (initial.Count == 0 || (add.Count == 0 && remove.Count == 0)) { RefreshTagSidebarTargets(); return; }
        _tagChangeBusy = true;
        _tagPredictedNames = PredictTagNames(initial.Select(p => Path.GetFileName(p)!), add, remove);
        try
        {
            var applied = await ApplyTagChangeCoreAsync(add, remove);
            while (applied && _tagChangeQueue.Count > 0)
            {
                var next = _tagChangeQueue.Dequeue();
                applied = await ApplyTagChangeCoreAsync(next.Add, next.Remove);
            }
        }
        finally
        {
            _tagChangeQueue.Clear();   // a failed change drops the ones queued behind it: they were meant for its result
            _tagPredictedNames = null;
            _tagChangeBusy = false;
            RefreshTagSidebarTargets();
        }
    }

    /// <summary>Renames the files the sidebar targets now. False when nothing could be changed (a problem was shown).</summary>
    private async Task<bool> ApplyTagChangeCoreAsync(IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var paths = TagTargetPaths();
        if (paths.Count == 0 || (add.Count == 0 && remove.Count == 0)) return true;
        var options = CurrentTagOptions;
        var selectedBefore = _workspace.Active is BrowserTabState ? SelectedExplorerEntries().Select(e => e.Path).ToList() : [];
        var plan = BatchRenamePlanner.PlanNames(paths.Select(p => new BatchRenameSource(p, false)).ToList(),
            (source, _) => TagFileName.Edit(Path.GetFileName(source.Path), add, remove, options), p => File.Exists(p) || Directory.Exists(p));
        if (plan.ProblemCount > 0)
        {
            var problem = plan.Items.First(i => i.HasProblem);
            ShowTagMessage($"Can't tag '{problem.OldName}': {problem.Message}", isError: true);
            return false;
        }
        if (plan.ChangedCount == 0) return true;

        var moves = plan.Moves;
        var result = await Task.Run(() => BatchRenameExecutor.Execute(moves));
        if (!result.Success)
        {
            _diagnostics.Write("file", "tag_rename_failed", new { count = moves.Count, error = result.Error });
            ShowTagMessage(result.Error ?? "Nothing was renamed.", isError: true);
            return false;
        }
        var map = result.Renamed.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
        if (_workspace.Active is BrowserTabState && ExplorerCurrentFolder is { } folder)
        {
            foreach (var from in map.Keys) _explorerCutPaths.Remove(from);
            FollowRenamedPictures(map);
            _explorerPendingSelection = selectedBefore.Select(p => map.TryGetValue(p, out var moved) ? moved : p).ToList();
            RefreshManagedBrowser(folder);
            await _explorerRefreshTask;
        }
        else if (map.Values.FirstOrDefault() is { } newPath)
        {
            try
            {
                _workspace.ReplaceActiveImage(newPath);
                await LoadPathAsync(newPath, updateActiveTab: true);
                RebuildTabStrip();
            }
            catch { /* the renamed file could not be reopened; the files are renamed all the same */ }
        }
        return true;
    }

    private void ShowTagMessage(string text, bool isError)
    {
        if (BrowserView.IsVisible && _browserSurface is not null) ShowExplorerMessage(text, isError);
        else if (ImageView.IsVisible) ShowViewerNotice(text);
        else if (_browserSurface is not null) ShowExplorerMessage(text, isError);
    }

    private Task OpenTagSettingsAsync() => OpenSettingsAsync();

    /// <summary>
    /// The context-menu entry for "Show / hide the tag sidebar", offered in the Explorer and viewer menus while tags are on (null
    /// otherwise). Same registry action as the Ctrl+Shift+G hotkey, so the shortcut text follows the user's binding.
    /// </summary>
    private MenuItem? BuildTagSidebarMenuItem()
    {
        if (!_settings.TagsEnabled) return null;
        var action = Glide.Core.Commands.AppActionRegistry.ForCommand(Glide.Core.Commands.GlideCommand.ToggleTagSidebar)
            ?? throw new InvalidOperationException("The tag sidebar action is not in the action registry");
        var label = TagSidebarHost.IsVisible ? "Hide the tag sidebar" : "Show the tag sidebar";
        var item = MenuActions.Tag(new MenuItem { Header = MenuActions.Header(label, ShortcutForAction(action.Id)), Icon = MenuIcon("Rename") }, action.Id);
        item.Click += async (_, _) => await ExecuteActionAsync(action, "menu");
        return item;
    }
}
