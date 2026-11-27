using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Explorer;
using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// 5.2.1 G2: "Delayed permanent delete (Shift+Delete with undo)", an opt-in setting (navigation.delayedPermanentDelete).
/// After the usual Shift+Delete confirmation the picture is hidden from every list (<see cref="PendingDeleteHideList"/>: the
/// viewer's folder list, the Explorer tab, the sibling-folder lists, tab previews), Glide shows the next picture, and the
/// file is deleted for good <see cref="PendingDeleteDelayMs"/> later unless "Undo" in its notice is clicked first. The file
/// stays where it is until then (no move to a temporary folder: a move could cross drives, change its path and time stamps,
/// and would leave a stray file behind after a crash). What is still pending when the window closes is deleted by
/// <see cref="FlushPendingDeletes"/>. A file that changed or vanished meanwhile is never deleted (<see cref="PendingPermanentDeleteQueue.Complete"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>How long a confirmed delayed delete waits. Tests shorten it.</summary>
    internal static int PendingDeleteDelayMs = 5000;
    private const int PendingDeleteInfoMs = 6000;

    private readonly PendingPermanentDeleteQueue _pendingDeletes = new();
    private readonly Dictionary<Guid, PendingDeleteNotice> _pendingDeleteNotices = new();

    private sealed record PendingDeleteNotice(Border Visual, bool TabClosed);

    internal PendingPermanentDeleteQueue PendingPermanentDeletes => _pendingDeletes;

    /// <summary>The real deletion (permanent, same code as the immediate Shift+Delete). Null on success, else the error message.</summary>
    internal static string? DeletePermanentlyNow(string path)
    {
        try
        {
            var result = ExplorerFileOperations.DeleteAsync([path], permanent: true).GetAwaiter().GetResult();
            if (result.Errors.Count > 0) return result.Errors[0].Message;
            return result.Deleted.Count == 0 ? "The file was not deleted." : null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Shift+Delete with the setting on, after the confirmation: hide, show the next picture, start the countdown.</summary>
    private async Task BeginDelayedPermanentDeleteAsync(string oldPath, string? folder, int oldIndex)
    {
        if (!File.Exists(oldPath))
        {
            var message = $"'{Path.GetFileName(oldPath)}' no longer exists.";
            _diagnostics.Write("file", "permanent_delete_failed", new { path = oldPath, error = message });
            await ConfirmAsync("Delete permanently", message, "OK", showCancel: false);
            return;
        }
        var entry = _pendingDeletes.Enqueue(oldPath);
        if (entry is null) return;   // already waiting for its deletion
        _diagnostics.Write("file", "permanent_delete_pending", new { path = oldPath, delayMs = PendingDeleteDelayMs });
        _ = RunPendingDeleteTimerAsync(entry.Id);
        var tabId = (_workspace.Active as ImageTabState)?.Id;
        await ShowNeighbourAfterDeleteAsync(folder, oldIndex);
        var tabClosed = tabId is { } id && _workspace.Tabs.All(t => t.Id != id);
        ShowPendingDeleteNotice(entry, tabClosed);
        RefreshExplorerListingFor(folder);
    }

    private async Task RunPendingDeleteTimerAsync(Guid id)
    {
        try { await Task.Delay(PendingDeleteDelayMs, _windowLifetimeCts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; /* the window is closing: FlushPendingDeletes finishes it */ }
        var result = await Task.Run(() => _pendingDeletes.Complete(id, DeletePermanentlyNow));
        ApplyPendingDeleteResult(result);
    }

    private void ApplyPendingDeleteResult(PendingDeleteResult result)
    {
        if (result.Entry is not { } entry || result.Outcome == PendingDeleteOutcome.NotPending) return;
        RemovePendingDeleteNotice(entry.Id);
        var name = Path.GetFileName(entry.Path);
        _diagnostics.Write("file", result.Outcome == PendingDeleteOutcome.Deleted ? "permanent_delete" : "permanent_delete_skipped",
            new { path = entry.Path, outcome = result.Outcome.ToString(), error = result.Error });
        var folder = Path.GetDirectoryName(entry.Path);
        switch (result.Outcome)
        {
            case PendingDeleteOutcome.Changed:
                RestoreToLists(entry.Path);
                ShowPendingDeleteInfo($"'{name}' changed, so it was not deleted.");
                break;
            case PendingDeleteOutcome.Failed:
                RestoreToLists(entry.Path);
                ShowPendingDeleteInfo($"Couldn't delete '{name}': {result.Error}");
                break;
            default:
                RefreshExplorerListingFor(folder);
                break;
        }
    }

    /// <summary>Undo clicked: the file is listed again and shown again; nothing is deleted.</summary>
    internal async Task UndoPendingDeleteAsync(Guid id)
    {
        _pendingDeleteNotices.TryGetValue(id, out var notice);
        var entry = _pendingDeletes.Undo(id);
        RemovePendingDeleteNotice(id);
        if (entry is null) return;   // too late: the deletion already started
        _diagnostics.Write("file", "permanent_delete_undone", new { path = entry.Path });
        RestoreToLists(entry.Path);
        try
        {
            if (!File.Exists(entry.Path)) return;
            if (_workspace.Active is ImageTabState)
            {
                await LoadPathAsync(entry.Path, updateActiveTab: true);
                RebuildTabStrip();
            }
            else if (notice?.TabClosed == true)
            {
                await OpenImageInNewTabAsync(entry.Path, new NewTabOpening(AtEnd: true, Activate: true), "undo_delete");
            }
        }
        catch (Exception ex) { _diagnostics.Write("file", "permanent_delete_undo_show_failed", new { path = entry.Path, error = ex.Message }); }
    }

    /// <summary>Puts a file that is no longer hidden back into the open folder list and the Explorer listing.</summary>
    private void RestoreToLists(string path)
    {
        try
        {
            var current = _navigator.Current;
            if (current is not null && string.Equals(Path.GetDirectoryName(current), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)
                && File.Exists(path) && _navigator.ApplyChanges([path], []))
                NotifyViewerStatusChanged();
        }
        catch { /* the list is rebuilt on the next folder change */ }
        RefreshExplorerListingFor(Path.GetDirectoryName(path));
    }

    private void RefreshExplorerListingFor(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (_workspace.Active is BrowserTabState browser && string.Equals(browser.Folder, folder, StringComparison.OrdinalIgnoreCase))
            RefreshManagedBrowser(folder);
    }

    /// <summary>Window closing or app exit: every pending file is deleted now (or left alone when it changed), never left half-handled.</summary>
    internal void FlushPendingDeletes()
    {
        if (_pendingDeletes.Count == 0) { _pendingDeletes.WaitForInFlight(TimeSpan.FromSeconds(10)); return; }
        var results = _pendingDeletes.CompleteAll(DeletePermanentlyNow);
        foreach (var result in results)
            if (result.Entry is { } entry)
                _diagnostics.Write("file", result.Outcome == PendingDeleteOutcome.Deleted ? "permanent_delete" : "permanent_delete_skipped",
                    new { path = entry.Path, outcome = result.Outcome.ToString(), error = result.Error, source = "flush" });
        foreach (var id in _pendingDeleteNotices.Keys.ToList()) RemovePendingDeleteNotice(id);
    }

    // ------------------------------------------------------------------ notices

    private void ShowPendingDeleteNotice(PendingDeleteEntry entry, bool tabClosed)
    {
        // Already completed or undone while the neighbour was loading: no notice.
        if (_pendingDeletes.Entries.All(e => e.Id != entry.Id)) return;
        var text = new TextBlock
        {
            Text = $"Deleting {Path.GetFileName(entry.Path)} permanently…", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340
        };
        var undo = new Button
        {
            Content = new TextBlock { Text = "Undo", FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextDecorations = TextDecorations.Underline },
            Focusable = false, IsTabStop = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Name = "PendingDeleteUndoButton"
        };
        var id = entry.Id;
        undo.Click += async (_, e) => { e.Handled = true; await UndoPendingDeleteAsync(id); };
        var visual = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EB15191E")), BorderBrush = new SolidColorBrush(Color.Parse("#FF5BB7F5")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 6, 6), Tag = id,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, undo } }
        };
        _pendingDeleteNotices[id] = new PendingDeleteNotice(visual, tabClosed);
        PendingDeleteNotices.Children.Add(visual);
        PositionPendingDeleteNotices();
    }

    /// <summary>A short message in the same stack (a delete that was skipped or failed); it removes itself.</summary>
    private void ShowPendingDeleteInfo(string message)
    {
        var visual = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EB15191E")), BorderBrush = new SolidColorBrush(Color.Parse("#FFE05050")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = message, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }
        };
        PendingDeleteNotices.Children.Add(visual);
        PositionPendingDeleteNotices();
        _ = Task.Delay(PendingDeleteInfoMs, _windowLifetimeCts.Token).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            PendingDeleteNotices.Children.Remove(visual);
            PositionPendingDeleteNotices();
        }), TaskScheduler.Default);
    }

    private void RemovePendingDeleteNotice(Guid id)
    {
        if (!_pendingDeleteNotices.Remove(id, out var notice)) return;
        PendingDeleteNotices.Children.Remove(notice.Visual);
        PositionPendingDeleteNotices();
    }

    private void PositionPendingDeleteNotices()
    {
        PendingDeleteNotices.IsVisible = PendingDeleteNotices.Children.Count > 0;
        var status = ViewerStatusSurface;
        var bottom = status.IsVisible && status.Bounds.Height > 0 ? status.Margin.Bottom + status.Bounds.Height + 10 : 24;
        PendingDeleteNotices.Margin = new Thickness(16, 0, 16, bottom);
    }
}
