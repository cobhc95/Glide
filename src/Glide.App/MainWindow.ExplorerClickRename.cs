using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Explorer tab click-to-rename (Windows File Explorer's "slow double-click", setting explorer.clickToRename, on by
/// default). A plain left click on the name text of the item that was already the one selected item, at least the system
/// double-click time after the previous click on it, arms a timer of the double-click time; when it fires the existing
/// inline rename starts (<see cref="BeginExplorerRename"/>, the F2 path: Enter commits, Escape cancels, focus loss commits).
/// A double-click or any other click before then, a drag, a selection or folder change, a scroll, a key press or the
/// window losing activation cancels it.
/// </summary>
public partial class MainWindow
{
    private ExplorerEntry? _explorerClickRenameArmed;      // armed on press; the timer starts on a release without a drag
    private ExplorerEntry? _explorerClickRenamePending;    // timer running for this entry
    private string? _explorerClickRenameFolder;
    private Guid _explorerClickRenameTab;
    private DispatcherTimer? _explorerClickRenameTimer;
    private string? _explorerLastClickPath;
    private long _explorerLastClickTick = long.MinValue / 2;

    /// <summary>Tests: replaces the system double-click time.</summary>
    internal TimeSpan? ExplorerDoubleClickTimeForTests { get; set; }

    /// <summary>Tests: whether a click-to-rename timer is running.</summary>
    internal bool ExplorerClickRenamePendingForTests => _explorerClickRenamePending is not null;

    /// <summary>The system double-click time (Win32 GetDoubleClickTime through Avalonia's platform settings).</summary>
    private TimeSpan ExplorerDoubleClickTime()
    {
        if (ExplorerDoubleClickTimeForTests is { } fixedTime) return fixedTime;
        try
        {
            if (PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) is { } time && time > TimeSpan.Zero) return time;
        }
        catch { /* platform settings unavailable: Windows' default below */ }
        return TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Called by the Explorer press handler for a left press on an item, before the selection changes:
    /// <paramref name="wasSoleSelected"/> is whether the item was the one selected item before this press.
    /// </summary>
    private void NoteExplorerItemLeftPress(ExplorerEntry entry, PointerPressedEventArgs e, bool wasSoleSelected)
    {
        // Any click (a double-click's second press included) cancels a pending rename.
        CancelExplorerClickRename();
        var now = Environment.TickCount64;
        var sinceLast = string.Equals(_explorerLastClickPath, entry.Path, StringComparison.OrdinalIgnoreCase) ? now - _explorerLastClickTick : long.MaxValue;
        _explorerLastClickPath = entry.Path;
        _explorerLastClickTick = now;
        if (!_settings.ExplorerClickToRename || !wasSoleSelected || e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None) return;
        if (sinceLast < (long)ExplorerDoubleClickTime().TotalMilliseconds) return; // too soon: that is a double-click's pace
        if (!IsOnExplorerNameText(e)) return;
        _explorerClickRenameArmed = entry;
    }

    /// <summary>A drag started from the press: no rename.</summary>
    private void NoteExplorerDragStarted() => _explorerClickRenameArmed = null;

    /// <summary>The release of the press: an armed plain click (no drag) starts the timer.</summary>
    private void NoteExplorerPointerReleased(PointerReleasedEventArgs e)
    {
        var armed = _explorerClickRenameArmed;
        _explorerClickRenameArmed = null;
        if (armed is null || e.InitialPressMouseButton != MouseButton.Left || _explorerDragging) return;
        if (_workspace.Active is not BrowserTabState browser) return;
        _explorerClickRenamePending = armed;
        _explorerClickRenameFolder = browser.Folder;
        _explorerClickRenameTab = browser.Id;
        _explorerClickRenameTimer ??= CreateExplorerClickRenameTimer();
        _explorerClickRenameTimer.Interval = ExplorerDoubleClickTime();
        _explorerClickRenameTimer.Stop();
        _explorerClickRenameTimer.Start();
    }

    private DispatcherTimer CreateExplorerClickRenameTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var entry = _explorerClickRenamePending;
            _explorerClickRenamePending = null;
            if (entry is null || !_settings.ExplorerClickToRename) return; // window deactivation already cancelled it
            if (_workspace.Active is not BrowserTabState browser || browser.Id != _explorerClickRenameTab ||
                !string.Equals(browser.Folder, _explorerClickRenameFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (_browserSelection.Count != 1 || !_browserSelection.Contains(entry.Path)) return;
            var index = ExplorerIndexOf(entry);
            if (index < 0) return;
            BeginExplorerRename(index);
        };
        return timer;
    }

    /// <summary>Cancels a pending (armed or timed) click-to-rename.</summary>
    private void CancelExplorerClickRename()
    {
        _explorerClickRenameArmed = null;
        _explorerClickRenamePending = null;
        _explorerClickRenameTimer?.Stop();
    }

    /// <summary>Selection changed: a pending rename for an item that is no longer the one selected item is cancelled.</summary>
    private void CancelExplorerClickRenameIfSelectionChanged()
    {
        var target = _explorerClickRenamePending ?? _explorerClickRenameArmed;
        if (target is null) return;
        if (_browserSelection.Count != 1 || !_browserSelection.Contains(target.Path)) CancelExplorerClickRename();
    }

    /// <summary>Hooks the cancel triggers that are not part of the press / release / key handlers (WireExplorerInput).</summary>
    private void WireExplorerClickRenameCancellation(Glide.App.Controls.BrowserSurface browser)
    {
        browser.ScrollViewer.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.X != 0 || e.OffsetDelta.Y != 0) CancelExplorerClickRename();
        };
        Deactivated += (_, _) => CancelExplorerClickRename();
    }

    /// <summary>
    /// True when the press landed on the item's name text: the caption under a thumbnail or icon, or the Name cell's text
    /// in Details, List and Small icons. Not the thumbnail or icon, and not the empty space around the text.
    /// </summary>
    private static bool IsOnExplorerNameText(PointerEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBlock { Tag: "name" } name) return PointIsOnText(name, e.GetPosition(name));
            if (visual is Panel { Tag: "namehost" } host)
                return host.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "name")) is { } text && PointIsOnText(text, e.GetPosition(text));
            if (visual is Border border && border.Classes.Contains("exTile")) return false;
        }
        return false;
    }

    private static bool PointIsOnText(TextBlock text, Point point)
    {
        if (string.IsNullOrEmpty(text.Text)) return false;
        try
        {
            var layout = text.TextLayout;
            var padding = text.Padding;
            var local = new Point(point.X - padding.Left, point.Y - padding.Top);
            // The laid-out lines (alignment applied): each line's own extent, so a click beside a short centred
            // caption is not on the text.
            var y = 0.0;
            foreach (var line in layout.TextLines)
            {
                var x = line.Start;
                if (local.Y >= y && local.Y < y + line.Height && local.X >= x && local.X <= x + line.WidthIncludingTrailingWhitespace) return true;
                y += line.Height;
            }
            return false;
        }
        catch
        {
            return new Rect(text.Bounds.Size).Contains(point);
        }
    }
}
