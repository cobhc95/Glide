using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.App.Platform;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>Managed Explorer tab, part 4: file operations (copy/cut/paste, delete, rename, new folder, properties).</summary>
public partial class MainWindow
{
    private string? _explorerRenamePath;
    private TextBox? _explorerRenameBox;
    private bool _explorerOperationBusy;
    private CancellationTokenSource? _explorerOperationCts;

    private string? ExplorerCurrentFolder => (_workspace.Active as BrowserTabState)?.Folder;

    private Task<bool> ExplorerConfirmAsync(string title, string message, string acceptText) =>
        ExplorerHost.Confirm is { } seam ? seam(title, message, acceptText) : ConfirmAsync(title, message, acceptText);

    private Task ExplorerShowErrorAsync(string title, string message) =>
        ExplorerHost.ShowError is { } seam ? seam(title, message) : ConfirmAsync(title, message, "OK", showCancel: false);

    // ------------------------------------------------------------------------------------------
    // Clipboard
    // ------------------------------------------------------------------------------------------

    private void ExplorerCopyOrCut(bool cut)
    {
        var paths = SelectedExplorerEntries().Select(e => e.Path).ToList();
        if (paths.Count == 0) return;
        if (!ExplorerFileClipboard.Current.TrySet(paths, cut))
        {
            ShowExplorerMessage("Couldn't place the items on the clipboard.", isError: true);
            return;
        }
        _explorerCutPaths.Clear();
        if (cut) foreach (var path in paths) _explorerCutPaths.Add(path);
        foreach (var tile in RealizedExplorerTiles()) UpdateExplorerTileVisual(tile);
        ShowExplorerMessage(paths.Count == 1
            ? $"{(cut ? "Cut" : "Copied")} '{Path.GetFileName(paths[0])}'"
            : $"{(cut ? "Cut" : "Copied")} {paths.Count} items");
    }

    private async Task ExplorerPasteAsync(string folder)
    {
        var content = ExplorerFileClipboard.Current.TryGet();
        if (content is null || content.Paths.Count == 0)
        {
            ShowExplorerMessage("There is nothing to paste.");
            return;
        }
        await ExplorerTransferAsync(content.Paths, folder, move: content.Cut, fromClipboard: true);
    }

    /// <summary>Copies or moves items into a folder off the UI thread, reports progress and errors, refreshes the list.</summary>
    private async Task ExplorerTransferAsync(IReadOnlyList<string> paths, string destination, bool move, bool fromClipboard = false)
    {
        if (_explorerOperationBusy)
        {
            ShowExplorerMessage("Another file operation is still running.", isError: true);
            return;
        }
        _explorerOperationBusy = true;
        _explorerOperationCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ExplorerProgress>(p =>
            {
                if (p.Total > 0 && !string.IsNullOrEmpty(p.CurrentName))
                    ShowExplorerMessage($"{p.Verb} {Math.Min(p.Done + 1, p.Total)} of {p.Total}: {p.CurrentName}");
            });
            var result = await ExplorerFileOperations.TransferAsync(paths, destination, move, progress, _explorerOperationCts.Token);
            if (move && fromClipboard)
            {
                ExplorerFileClipboard.Current.Clear();
                _explorerCutPaths.Clear();
            }
            var current = ExplorerCurrentFolder;
            var sameFolder = current is not null && string.Equals(Path.GetFullPath(current).TrimEnd('\\'), Path.GetFullPath(destination).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (current is not null)
            {
                if (sameFolder && result.Created.Count > 0) _explorerPendingSelection = result.Created.ToList();
                RefreshManagedBrowser(current);
                await _explorerRefreshTask;
            }
            var verb = move ? "Moved" : "Copied";
            ShowExplorerMessage(result.Errors.Count == 0
                ? $"{verb} {result.Created.Count} item{(result.Created.Count == 1 ? "" : "s")}"
                : $"{verb} {result.Created.Count}, {result.Errors.Count} failed: {result.Errors[0].Message}", isError: result.Errors.Count > 0);
            if (result.Errors.Count > 0)
                await ExplorerShowErrorAsync(move ? "Move" : "Copy", string.Join("\n", result.Errors.Take(8).Select(er => er.Message)));
        }
        catch (Exception ex)
        {
            ShowExplorerMessage($"The operation failed: {ex.Message}", isError: true);
        }
        finally
        {
            _explorerOperationBusy = false;
            _explorerOperationCts?.Dispose();
            _explorerOperationCts = null;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------------------------------

    private async Task ExplorerDeleteSelectionAsync(bool permanent)
    {
        var selected = SelectedExplorerEntries();
        if (selected.Count == 0 || _explorerOperationBusy) return;
        var what = selected.Count == 1 ? $"'{selected[0].Name}'" : $"these {selected.Count} items";
        bool accepted;
        if (permanent)
            accepted = await ExplorerConfirmAsync("Delete permanently",
                $"Permanently delete {what}?\n\nThis can't be undone and {(selected.Count == 1 ? "it" : "they")} will NOT go to the Recycle Bin.", "Delete permanently");
        else
            accepted = await ExplorerConfirmAsync("Delete",
                $"Move {what} to the Recycle Bin?", "Delete");
        if (!accepted) return;

        var firstIndex = _browserEntries.FindIndex(e => _browserSelection.Contains(e.Path));
        _explorerOperationBusy = true;
        try
        {
            var progress = new Progress<ExplorerProgress>(p =>
            {
                if (p.Total > 0 && !string.IsNullOrEmpty(p.CurrentName))
                    ShowExplorerMessage($"{p.Verb} {Math.Min(p.Done + 1, p.Total)} of {p.Total}: {p.CurrentName}");
            });
            var result = await ExplorerFileOperations.DeleteAsync(selected.Select(e => e.Path).ToList(), permanent, progress);
            _explorerCutPaths.ExceptWith(result.Deleted);
            if (ExplorerCurrentFolder is { } folder)
            {
                _browserSelection.Clear();
                RefreshManagedBrowser(folder);
                await _explorerRefreshTask;
                // Explorer selects the item that moved into the deleted item's place.
                if (_browserEntries.Count > 0 && firstIndex >= 0)
                    SelectBrowserIndex(Math.Min(firstIndex, _browserEntries.Count - 1), false, false, true);
            }
            ShowExplorerMessage(result.Errors.Count == 0
                ? $"Deleted {result.Deleted.Count} item{(result.Deleted.Count == 1 ? "" : "s")}"
                : $"Deleted {result.Deleted.Count}, {result.Errors.Count} failed: {result.Errors[0].Message}", isError: result.Errors.Count > 0);
            if (result.Errors.Count > 0)
                await ExplorerShowErrorAsync("Delete", string.Join("\n", result.Errors.Take(8).Select(er => er.Message)));
        }
        catch (Exception ex)
        {
            ShowExplorerMessage($"Delete failed: {ex.Message}", isError: true);
        }
        finally { _explorerOperationBusy = false; }
    }

    // ------------------------------------------------------------------------------------------
    // New folder, rename
    // ------------------------------------------------------------------------------------------

    private async Task ExplorerNewFolderAsync()
    {
        if (ExplorerCurrentFolder is not { } folder || _browserSurface is null) return;
        string created;
        try { created = ExplorerFileOperations.CreateFolder(folder); }
        catch (Exception ex)
        {
            ShowExplorerMessage($"Couldn't create a folder here: {ExplorerFileOperations.DescribeError(ex, folder)}", isError: true);
            return;
        }
        // The new folder must be listed to be renamed: drop any filter.
        if (!string.IsNullOrEmpty(_explorerFilterText)) { _explorerFilterText = ""; SetExplorerFilterBoxText(""); }
        _explorerPendingSelection = new List<string> { created };
        _explorerPendingRename = created;
        RefreshManagedBrowser(folder);
        await _explorerRefreshTask;
    }

    private void ExplorerBeginRenameSelection()
    {
        var selected = SelectedExplorerEntries();
        // Several items: one dialog renames them all (a pattern, a live preview, all or nothing).
        if (selected.Count > 1) { _ = ExplorerBatchRenameAsync(selected); return; }
        var target = selected.Count == 1 ? selected[0]
            : _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count && selected.Count > 0 ? _browserEntries[_browserCurrentIndex] : null;
        if (target is null) return;
        BeginExplorerRename(ExplorerIndexOf(target));
    }

    private void BeginExplorerRename(int index)
    {
        if ((uint)index >= (uint)_browserEntries.Count) return;
        CommitOrCancelExplorerRename(commit: true);
        var entry = _browserEntries[index];
        _explorerRenamePath = entry.Path;
        EnsureExplorerIndexVisible(index);
        if (ExplorerTileFor(index) is { } tile) AttachExplorerRenameEditor(tile, entry);
        // Otherwise the tile is not realized yet: BrowserRepeaterElementPrepared attaches the editor.
    }

    private void AttachExplorerRenameEditor(Border tile, ExplorerEntry entry)
    {
        if (!string.Equals(_explorerRenamePath, entry.Path, StringComparison.OrdinalIgnoreCase)) return;
        var host = tile.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Name == "NameHost");
        if (host is null || host.Children.OfType<TextBox>().Any()) return;
        var label = host.Children.OfType<TextBlock>().FirstOrDefault();
        var box = new TextBox
        {
            Name = "RenameBox",
            Text = entry.Name,
            FontSize = 12.5,
            Padding = new Thickness(4, 2),
            MinHeight = 22,
            AcceptsReturn = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = ExplorerViewModes.IsIconMode(_browserViewMode) ? TextAlignment.Center : TextAlignment.Left
        };
        if (ExplorerViewModes.IsIconMode(_browserViewMode)) { box.TextWrapping = TextWrapping.Wrap; box.AcceptsReturn = false; }
        if (label is not null) label.IsVisible = false;
        host.Children.Add(box);
        _explorerRenameBox = box;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitOrCancelExplorerRename(commit: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; CommitOrCancelExplorerRename(commit: false); _browserSurface?.ScrollViewer.Focus(); }
        };
        var name = entry.Name;
        var end = !entry.IsDirectory && Path.GetFileNameWithoutExtension(name).Length > 0 ? Path.GetFileNameWithoutExtension(name).Length : name.Length;
        var hadFocus = false;
        box.GotFocus += (_, _) => hadFocus = true;
        box.LostFocus += (_, _) =>
        {
            // Clicking elsewhere commits a valid name and abandons an invalid one (only once the editor really had focus).
            if (!hadFocus) return;
            Dispatcher.UIThread.Post(() =>
            {
                // A tile that ItemsRepeater is re-realizing also loses focus; it re-attaches its editor when prepared.
                if (!ReferenceEquals(_explorerRenameBox, box) || !box.IsAttachedToVisualTree() || box.IsKeyboardFocusWithin) return;
                // A real click on the list commits before focus moves (see BrowserItemPointerPressed). Focus that fell back
                // to the list by itself (window re-activation, a refresh) just returns to the editor.
                // Only focus that moved to another control of the Explorer toolbar (address bar, filter, buttons) ends the edit.
                var focused = TopLevel.GetTopLevel(box)?.FocusManager?.GetFocusedElement() as Visual;
                var inToolbar = _browserSurface is { } surface && focused is not null &&
                    focused.GetSelfAndVisualAncestors().Any(a => ReferenceEquals(a, surface)) &&
                    !focused.GetSelfAndVisualAncestors().Any(a => ReferenceEquals(a, surface.ScrollViewer));
                if (!inToolbar)
                {
                    FocusRenameBox(box, end, attemptsLeft: 4);
                    return;
                }
                CommitOrCancelExplorerRename(commit: true, abandonInvalid: true);
            }, DispatcherPriority.Background);
        };
        FocusRenameBox(box, end, attemptsLeft: 8);
    }

    /// <summary>Focuses the inline editor; a tile that was only just realized may not be attached yet, so retry a few times.</summary>
    private void FocusRenameBox(TextBox box, int selectionEnd, int attemptsLeft)
    {
        if (!ReferenceEquals(_explorerRenameBox, box)) return;
        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd = selectionEnd;
        if (box.IsFocused || attemptsLeft <= 0) return;
        Dispatcher.UIThread.Post(() => FocusRenameBox(box, selectionEnd, attemptsLeft - 1), DispatcherPriority.Background);
    }

    /// <summary>Ends inline rename. Returns false only when a commit was refused (invalid name) and editing continues.</summary>
    private bool CommitOrCancelExplorerRename(bool commit, bool abandonInvalid = false)
    {
        var path = _explorerRenamePath;
        var box = _explorerRenameBox;
        if (path is null || box is null)
        {
            _explorerRenamePath = null;
            return true;
        }
        var folder = Path.GetDirectoryName(path)!;
        var newName = box.Text ?? "";
        if (commit && !string.Equals(newName, Path.GetFileName(path), StringComparison.Ordinal))
        {
            var error = ExplorerFileOperations.ValidateName(newName, folder, path);
            if (error is not null)
            {
                if (abandonInvalid) { ShowExplorerMessage(error, isError: true); EndExplorerRenameEditor(box); return true; }
                box.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
                ToolTip.SetTip(box, error);
                ToolTip.SetIsOpen(box, true);
                ShowExplorerMessage(error, isError: true);
                return false;
            }
            try
            {
                var renamed = ExplorerFileOperations.Rename(path, newName);
                _explorerCutPaths.Remove(path);
                FollowRenamedPictures(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = renamed });
                EndExplorerRenameEditor(box);
                _explorerPendingSelection = new List<string> { renamed };
                RefreshManagedBrowser(folder);
                ShowExplorerMessage($"Renamed to '{Path.GetFileName(renamed)}'");
                return true;
            }
            catch (Exception ex)
            {
                var message = ex is ExplorerOperationException ? ex.Message : ExplorerFileOperations.DescribeError(ex, path);
                if (abandonInvalid) { ShowExplorerMessage(message, isError: true); EndExplorerRenameEditor(box); return true; }
                box.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
                ToolTip.SetTip(box, message);
                ShowExplorerMessage(message, isError: true);
                return false;
            }
        }
        EndExplorerRenameEditor(box);
        return true;
    }

    private void EndExplorerRenameEditor(TextBox box)
    {
        _explorerRenamePath = null;
        _explorerRenameBox = null;
        if (box.Parent is Panel host)
        {
            host.Children.Remove(box);
            foreach (var label in host.Children.OfType<TextBlock>()) label.IsVisible = true;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Properties, open with, copy path
    // ------------------------------------------------------------------------------------------

    private void ShowExplorerProperties(ExplorerEntry? entry = null)
    {
        var target = entry?.Path ?? ExplorerFocusedSelectedEntry()?.Path ?? ExplorerCurrentFolder;
        if (target is null) return;
        if (!WindowsShellProperties.Show(target))
            ShowExplorerMessage($"Couldn't show the properties of '{Path.GetFileName(target.TrimEnd('\\'))}'.", isError: true);
    }

    /// <summary>The focused item when it is selected (it has the focus border), otherwise the first selected item.</summary>
    private ExplorerEntry? ExplorerFocusedSelectedEntry() =>
        ExplorerCurrentEntry is { } current && _browserSelection.Contains(current.Path) ? current : SelectedExplorerEntries().FirstOrDefault();

    private void ExplorerOpenWith(string path)
    {
        if (ExplorerHost.OpenWith is { } seam) seam(path);
        else WindowsOpenWith.Show(path);
    }

    private async Task ExplorerCopyPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var text = string.Join(Environment.NewLine, paths);
        ExplorerHost.LastCopiedText = text;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        }
        catch { /* clipboard can be locked by another app; nothing to recover */ }
        ShowExplorerMessage(paths.Count == 1 ? "Copied the path" : $"Copied {paths.Count} paths");
    }
}
