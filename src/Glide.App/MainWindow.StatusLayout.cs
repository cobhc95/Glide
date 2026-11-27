using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Controls;

using Glide.App.Services;
using Glide.Core.Commands;

namespace Glide.App;

/// <summary>Adaptive status-bar layout: wide row, header + balanced rows, or overflow menu.</summary>
public partial class MainWindow
{
    private sealed record StatusSlot(
        string Id,
        Border Border,
        Control Inner,
        int HideOrder,
        Func<IEnumerable<(string Icon, string Label, Button Button)>> Entries);

    private IReadOnlyList<StatusSlot>? _statusSlots;
    private readonly List<StatusSlot> _statusHiddenSlots = new();
    private int _statusArrangePosted;
    private int _statusStatsLength = -1;

    // Display order. HideOrder is the overflow priority: 1 moves into the "..." menu first, 0 never does.
    private IReadOnlyList<StatusSlot> StatusSlots => _statusSlots ??= new StatusSlot[]
    {
        new("zoom", ZoomGroupBorder, ZoomGroup, 7, () => new[] { ("ZoomOut", "Zoom out", StatusZoomOutButton), ("ZoomIn", "Zoom in", StatusZoomInButton) }),
        new("fit", FitGroupBorder, FitGroup, 5, () => new[] { ("Fit", "Fit image", StatusFitImageButton), ("FitWindow", "Fit to window", StatusFitWindowButton), ("FitWidth", "Fit width", StatusFitWidthButton), ("FitHeight", "Fit height", StatusFitHeightButton) }),
        new("info", InfoGroupBorder, InfoGroup, 4, () => new[] { ("Info", "Image information", StatusInfoButton) }),
        new("options", OptionsGroupBorder, OptionsGroup, 2, () => new[] { ("Settings", "Settings", StatusOptionsButton) }),
        new("save", SaveGroupBorder, SaveGroup, 3, () => new[] { ("SaveFile", "Save", StatusSaveButton), ("SaveAs", "Save as…", StatusSaveAsButton) }),
        new("overlay", OverlayGroupBorder, OverlayGroup, 1, () => new[]
        {
            ("OverlayAdd", "Add Window-in-Window overlay", OverlayAddButton),
            ("OverlayLoad", "Load overlay layout", OverlayLoadButton),
            ("OverlaySave", "Save overlay layout", OverlaySaveButton),
            ("Close", "Clear all overlays", OverlayClearButton)
        }),
        // Shown only while an animated GIF is open (GifGroup.IsVisible, see UpdateGifStatusUi).
        new("gif", GifGroupBorder, GifGroup, 4, () => new[] { ("FramePrevious", "Previous GIF frame", GifPreviousFrameButton), (_gifPlayer?.IsPaused == true ? "Play" : "Pause", _gifPlayer?.IsPaused == true ? "Play GIF" : "Pause GIF", GifPlayPauseButton), ("FrameNext", "Next GIF frame", GifNextFrameButton) }),
        new("slideshow", SlideshowGroupBorder, SlideshowGroup, 6, () => new[] { ("Play", "Slideshow play / pause", SlideshowButton), ("Stop", "Stop slideshow", SlideshowStopButton) }),
        new("nav", NavGroupBorder, NavigationGroup, 0, () => Array.Empty<(string, string, Button)>())
    };

    // extra single-action buttons the user added (Customize status-bar buttons). Built on first use, then reused.
    private readonly Dictionary<string, StatusSlot> _extraStatusSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Button, string> _extraStatusButtons = new();

    /// <summary>An added action button, styled exactly like the built-in status icons (classes statusIcon / legacyStatusIcon / statusGlyph).</summary>
    private StatusSlot? ExtraStatusSlot(string id)
    {
        if (_extraStatusSlots.TryGetValue(id, out var existing)) return existing;
        if (Glide.Core.StatusBarGroupCatalog.FindAction(id) is not { } definition || AppActionRegistry.Find(definition.ActionId) is not { } action) return null;
        var button = new Button { Content = new GlideIconView { Kind = definition.Icon } };
        button.Classes.Add("statusIcon");
        button.Classes.Add("legacyStatusIcon");
        ((GlideIconView)button.Content).Classes.Add("statusGlyph");
        var shortcut = ShortcutForAction(action.Id);
        ToolTip.SetTip(button, string.IsNullOrWhiteSpace(shortcut) ? definition.Label : $"{definition.Label} ({shortcut})");
        button.Click += async (_, _) => await ExecuteActionAsync(action, "status_bar");
        var inner = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
        inner.Children.Add(button);
        var border = new Border { Child = inner };
        border.Classes.Add("statusGroup");
        border.Classes.Add("extraGroup");
        var slot = new StatusSlot(id, border, inner, 1, () => new[] { (definition.Icon, definition.Label, button) });
        _extraStatusSlots[id] = slot;
        _extraStatusButtons[button] = action.Id;
        if (string.Equals(action.Id, LoopFolderActionId, StringComparison.OrdinalIgnoreCase)) UpdateLoopFolderStatusButton();
        return slot;
    }

    /// <summary>Re-runs the layout when the stats text length changed enough to matter.</summary>
    private void ScheduleStatusRearrangeForStats(int statsLength)
    {
        if (statsLength == _statusStatsLength) return;
        _statusStatsLength = statsLength;
        if (Interlocked.Exchange(ref _statusArrangePosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _statusArrangePosted, 0);
            ApplyStatusBarSize();
        }, DispatcherPriority.Background);
    }

    private void ArrangeStatusControls(double availableWidth, bool showStats, double statsCap, double iconSize)
    {
        if (StatusRow1 is null || StatusRow2 is null || StatusLayoutGrid is null) return;
        const double Gap = 4d, ColGap = 8d;
        var inner = Math.Max(80d, availableWidth - 14d - 10d);
        // the user's order and choice of groups (Customize status-bar buttons).
        var shownIds = Glide.Core.StatusBarGroupCatalog.Parse(StatusBarOptionsView.Groups);
        foreach (var hiddenSlot in StatusSlots.Where(x => !shownIds.Contains(x.Id))) hiddenSlot.Border.IsVisible = false;
        var slots = shownIds.Select(id => StatusSlots.FirstOrDefault(x => x.Id == id) ?? ExtraStatusSlot(id)).OfType<StatusSlot>().ToList();

        // Every group must be attached to the (visible) tree to be styled and measured at its natural size. Groups already
        // sitting in a row stay where they are: detaching and re-attaching all of them on every call re-ran the styles and a
        // full measure of the whole bar for each WM_SIZE of a window drag. Only a group that is in no row (moved into the
        // "..." menu earlier) is put on row 1 for the measurement; the final layout below is reconciled without churn.
        var iconChanged = _statusIconSizeApplied != iconSize;
        bool InRow(Control c) => c.Parent is Panel p && (ReferenceEquals(p, StatusRow1) || ReferenceEquals(p, StatusRow2));
        foreach (var slot in slots)
        {
            slot.Border.IsVisible = slot.Inner.IsVisible;
            var attached = InRow(slot.Border);
            if (iconChanged || !attached) ApplyStatusIconSize(slot.Border, iconSize, legacyOnly: true);
            if (!attached) StatusRow1.Children.Add(slot.Border);
        }
        var moreAttached = InRow(MoreGroupBorder);
        if (iconChanged || !moreAttached) ApplyStatusIconSize(MoreGroupBorder, iconSize, legacyOnly: false);
        _statusIconSizeApplied = iconSize;
        MoreGroupBorder.IsVisible = true;
        if (!moreAttached) StatusRow1.Children.Add(MoreGroupBorder);

        static double Natural(Border border, double icon)
        {
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = border.DesiredSize.Width;
            if (width > 1) return width;
            var count = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(border).OfType<Button>().Count(b => b.IsVisible);
            return count * (icon + 2) + 10;
        }

        var enabled = slots.Where(s => s.Border.IsVisible).ToList();
        var widths = enabled.Select(s => Natural(s.Border, iconSize)).ToArray();
        var moreWidth = Natural(MoreGroupBorder, iconSize);
        MoreGroupBorder.IsVisible = false;

        double RowWidth(IEnumerable<int> indexes)
        {
            double sum = 0;
            var count = 0;
            foreach (var i in indexes) { sum += widths[i]; count++; }
            return count == 0 ? 0 : sum + Gap * (count - 1);
        }

        double statsWidth = 0;
        if (showStats)
        {
            StatusStatsText.TextWrapping = TextWrapping.NoWrap;
            StatusStatsText.TextTrimming = TextTrimming.CharacterEllipsis;
            StatusStatsText.MaxLines = 0;
            StatusStatsText.MaxWidth = statsCap;
            StatusStatsText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            statsWidth = StatusStatsText.DesiredSize.Width;
        }
        StatusStatsHost.IsVisible = showStats;
        // the minimise button is always there (auto-hide shows it with its own glyph).
        StatusCollapseButton.IsVisible = true;
        StatusWindowControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var windowControlsWidth = StatusWindowControls.DesiredSize.Width;

        static void Place(Control control, int row, int column, int span)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, span);
        }

        var all = Enumerable.Range(0, enabled.Count).ToList();
        var allWidth = RowWidth(all);
        // "Always stack the information on a separate line" (default): the info text keeps its own top line (with the window
        // buttons at its right) and the button groups sit below it even when everything would fit on one line. It needs a
        // visible info text: with no stats to show (a very narrow window, or nothing to display) the single-row logic applies.
        var stacked = StatusBarOptionsView.StackInfoLine && showStats && !string.IsNullOrWhiteSpace(StatusStatsText.Text);
        var wide = !stacked && allWidth + ColGap + windowControlsWidth + (showStats ? statsWidth + ColGap : 0d) <= inner;

        List<int> row1 = all, row2 = new();
        var overflow = false;
        _statusHiddenSlots.Clear();

        if (wide)
        {
            Place(StatusStatsHost, 0, 0, 1);
            Place(StatusControlsPanel, 0, 1, 1);
            Place(StatusWindowControls, 0, 2, 1);
            // Single row: the stats stay on one line (trimmed only if they exceed their share).
        }
        else
        {
            Place(StatusStatsHost, 0, 0, 2);
            Place(StatusWindowControls, 0, 2, 1);
            Place(StatusControlsPanel, 1, 0, 3);
            if (showStats)
            {
                // Header line: wrap only at the separators (segments use non-breaking spaces).
                StatusStatsText.TextWrapping = TextWrapping.Wrap;
                // Stacked: a name longer than the bar wraps at most onto a second line and then ends in an ellipsis.
                StatusStatsText.TextTrimming = stacked ? TextTrimming.CharacterEllipsis : TextTrimming.None;
                StatusStatsText.MaxLines = stacked ? 2 : 0;
                StatusStatsText.MaxWidth = Math.Max(60d, inner - windowControlsWidth - ColGap);
            }

            if (allWidth > inner)
            {
                // Balanced two rows: split the ordered groups where the longer row is shortest.
                var bestSplit = -1;
                var bestMax = double.MaxValue;
                for (var k = 1; k < all.Count; k++)
                {
                    var longest = Math.Max(RowWidth(all.Take(k)), RowWidth(all.Skip(k)));
                    if (longest < bestMax) { bestMax = longest; bestSplit = k; }
                }

                if (bestSplit > 0 && bestMax <= inner)
                {
                    row1 = all.Take(bestSplit).ToList();
                    row2 = all.Skip(bestSplit).ToList();
                }
                else
                {
                    // One row plus the "..." menu: hide the lowest-priority groups until it fits.
                    overflow = true;
                    row1 = new List<int>(all);
                    while (row1.Count > 1 && RowWidth(row1) + Gap + moreWidth > inner)
                    {
                        var victim = row1.Where(i => enabled[i].HideOrder > 0).OrderBy(i => enabled[i].HideOrder).Cast<int?>().FirstOrDefault();
                        if (victim is null) break;
                        row1.Remove(victim.Value);
                        _statusHiddenSlots.Add(enabled[victim.Value]);
                    }
                    _statusHiddenSlots.Sort((a, b) => slots.IndexOf(a).CompareTo(slots.IndexOf(b)));
                }
            }
        }

        foreach (var hidden in _statusHiddenSlots) hidden.Border.IsVisible = false;
        MoreGroupBorder.IsVisible = overflow;
        var desiredRow1 = new List<Control>(row1.Count + 1);
        foreach (var i in row1) desiredRow1.Add(enabled[i].Border);
        // The "..." group stays in row 1 (invisible unless something overflowed, so it takes no space): taking it out and putting it
        // back for its measurement on every call re-attached it, and a changed child list made the panel rebuild every time.
        desiredRow1.Add(MoreGroupBorder);
        var desiredRow2 = new List<Control>(row2.Count);
        foreach (var i in row2) desiredRow2.Add(enabled[i].Border);
        SyncStatusRows(StatusRow1, desiredRow1, StatusRow2, desiredRow2);
        StatusRow2.IsVisible = row2.Count > 0;
    }

    private double _statusIconSizeApplied = double.NaN;

    private static void ApplyStatusIconSize(Control root, double iconSize, bool legacyOnly)
    {
        foreach (var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>())
        {
            if (legacyOnly && !button.Classes.Contains("legacyStatusIcon")) continue;
            button.Width = iconSize;
            button.Height = iconSize;
        }
    }

    private static bool RowHolds(Panel row, IReadOnlyList<Control> desired)
    {
        var children = row.Children;
        if (children.Count != desired.Count) return false;
        for (var i = 0; i < desired.Count; i++)
            if (!ReferenceEquals(children[i], desired[i])) return false;
        return true;
    }

    /// <summary>Makes <paramref name="row"/> hold exactly <paramref name="desired"/>, in order, touching the panel only when it differs.</summary>
    internal static void SyncStatusRow(Panel row, IReadOnlyList<Control> desired) => SyncStatusRows(row, desired, null, Array.Empty<Control>());

    /// <summary>
    /// Both rows hold their groups already: nothing happens. Otherwise both are emptied first and then refilled, because a group
    /// that moves from one row to the other still has its old parent until that row lets go of it.
    /// </summary>
    internal static void SyncStatusRows(Panel row1, IReadOnlyList<Control> desired1, Panel? row2, IReadOnlyList<Control> desired2)
    {
        if (RowHolds(row1, desired1) && (row2 is null || RowHolds(row2, desired2))) return;
        row1.Children.Clear();
        row2?.Children.Clear();
        foreach (var control in desired1) row1.Children.Add(control);
        if (row2 is not null) foreach (var control in desired2) row2.Children.Add(control);
    }

    private void StatusMoreClicked(object? sender, RoutedEventArgs e)
    {
        var items = new List<object>();
        foreach (var slot in _statusHiddenSlots)
        {
            var group = new List<MenuItem>();
            foreach (var (icon, label, button) in slot.Entries())
            {
                if (!button.IsVisible) continue;
                var item = new MenuItem { Header = label, Icon = MenuIcon(icon), IsEnabled = button.IsEnabled };
                item = StatusButtonActionIds().TryGetValue(button, out var actionId) && !actionId.StartsWith(MenuActions.ContextPrefix, StringComparison.Ordinal)
                    ? MenuActions.Tag(item, actionId)
                    : MenuActions.Context(item, "status.overflowButton");
                var target = button;
                item.Click += async (_, _) => await ExecuteStatusButtonAsync(target);
                group.Add(item);
            }
            if (group.Count == 0) continue;
            if (items.Count > 0) items.Add(new MenuItem { Header = "-" });
            items.AddRange(group);
        }
        if (items.Count == 0) return;
        PrepareOwnedContextMenu(new ContextMenu { ItemsSource = items }).Open(StatusMoreButton);
    }

    /// <summary>
    /// Every status-bar button and the registry action it performs (or a context tag). The status-bar coverage
    /// test fails when a button is added to the bar without an entry here, so each button is reachable from the
    /// hotkey list, gestures and Settings search through its registry action.
    /// </summary>
    internal IReadOnlyDictionary<Button, string> StatusButtonActionIds() => new Dictionary<Button, string>
    {
        [StatusZoomOutButton] = "view.zoomOut", [StatusZoomInButton] = "view.zoomIn",
        [StatusFitImageButton] = "view.fit", [StatusFitWindowButton] = "view.fitWindow", [StatusFitWidthButton] = "view.fitWidth", [StatusFitHeightButton] = "view.fitHeight",
        [StatusInfoButton] = "view.info", [StatusOptionsButton] = "app.settings",
        [StatusSaveButton] = "file.save", [StatusSaveAsButton] = "file.saveAs",
        [OverlaySaveButton] = "overlay.saveLayout", [OverlayClearButton] = "overlay.clear", [OverlayAddButton] = "overlay.add", [OverlayLoadButton] = "overlay.loadLayout",
        [GifPreviousFrameButton] = "gif.previousFrame", [GifPlayPauseButton] = "gif.playPause", [GifNextFrameButton] = "gif.nextFrame",
        [SlideshowButton] = "slideshow.startPause", [SlideshowStopButton] = "slideshow.stop",
        [StatusFirstButton] = "nav.first", [StatusPreviousButton] = "nav.previous", [StatusNextButton] = "nav.next", [StatusLastButton] = "nav.last",
        [StatusCollapseButton] = "status.toggle", [StatusCloseButton] = "status.close", [StatusActualButton] = "view.actual",
        [StatusRestoreButton] = "status.toggle",
        [StatusMoreButton] = MenuActions.ContextPrefix + "status.overflowMenu (lists the buttons that do not fit)",
    }.Concat(_extraStatusButtons).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>A small vector icon in the toolbar's own style, tinted with the theme text colour.</summary>
    private Control MenuIcon(string kind) => MenuIcons.Create(this, kind);
}
