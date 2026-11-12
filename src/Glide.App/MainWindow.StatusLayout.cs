using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Controls;

namespace Glide.App;

/// <summary>Adaptive status-bar layout: wide row, header + balanced rows, or overflow menu.</summary>
public partial class MainWindow
{
    private sealed record StatusSlot(
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
        new(ZoomGroupBorder, ZoomGroup, 7, () => new[] { ("ZoomOut", "Zoom out", StatusZoomOutButton), ("ZoomIn", "Zoom in", StatusZoomInButton) }),
        new(FitGroupBorder, FitGroup, 5, () => new[] { ("Fit", "Fit image", StatusFitImageButton), ("FitWindow", "Fit to window", StatusFitWindowButton), ("FitWidth", "Fit width", StatusFitWidthButton), ("FitHeight", "Fit height", StatusFitHeightButton) }),
        new(InfoGroupBorder, InfoGroup, 4, () => new[] { ("Info", "Image information", StatusInfoButton) }),
        new(OptionsGroupBorder, OptionsGroup, 2, () => new[] { ("Settings", "Settings", StatusOptionsButton) }),
        new(SaveGroupBorder, SaveGroup, 3, () => new[] { ("SaveFile", "Save", StatusSaveButton), ("SaveAs", "Save as…", StatusSaveAsButton) }),
        new(OverlayGroupBorder, OverlayGroup, 1, () => new[]
        {
            ("OverlayAdd", "Add Window-in-Window overlay", OverlayAddButton),
            ("OverlayLoad", "Load overlay layout", OverlayLoadButton),
            ("OverlaySave", "Save overlay layout", OverlaySaveButton),
            ("Close", "Clear all overlays", OverlayClearButton)
        }),
        new(SlideshowGroupBorder, SlideshowGroup, 6, () => new[] { ("Play", "Slideshow play / pause", SlideshowButton), ("Stop", "Stop slideshow", SlideshowStopButton) }),
        new(NavGroupBorder, NavigationGroup, 0, () => Array.Empty<(string, string, Button)>())
    };

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
        var slots = StatusSlots;

        // Put every group back on row 1 so each can be measured at its natural size.
        StatusRow1.Children.Clear();
        StatusRow2.Children.Clear();
        foreach (var slot in slots)
        {
            slot.Border.IsVisible = slot.Inner.IsVisible;
            foreach (var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(slot.Border).OfType<Button>().Where(b => b.Classes.Contains("legacyStatusIcon")))
            {
                button.Width = iconSize;
                button.Height = iconSize;
            }
            StatusRow1.Children.Add(slot.Border);
        }
        foreach (var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(MoreGroupBorder).OfType<Button>())
        {
            button.Width = iconSize;
            button.Height = iconSize;
        }
        MoreGroupBorder.IsVisible = true;
        StatusRow1.Children.Add(MoreGroupBorder);

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
            StatusStatsText.MaxWidth = statsCap;
            StatusStatsText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            statsWidth = StatusStatsText.DesiredSize.Width;
        }
        StatusStatsHost.IsVisible = showStats;
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
        var wide = allWidth + ColGap + windowControlsWidth + (showStats ? statsWidth + ColGap : 0d) <= inner;

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
                StatusStatsText.TextTrimming = TextTrimming.None;
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
                    _statusHiddenSlots.Sort((a, b) => slots.ToList().IndexOf(a).CompareTo(slots.ToList().IndexOf(b)));
                }
            }
        }

        StatusRow1.Children.Clear();
        StatusRow2.Children.Clear();
        foreach (var i in row1) StatusRow1.Children.Add(enabled[i].Border);
        foreach (var i in row2) StatusRow2.Children.Add(enabled[i].Border);
        foreach (var hidden in _statusHiddenSlots) hidden.Border.IsVisible = false;
        if (overflow)
        {
            MoreGroupBorder.IsVisible = true;
            StatusRow1.Children.Add(MoreGroupBorder);
        }
        StatusRow2.IsVisible = row2.Count > 0;
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

    /// <summary>A small vector icon in the toolbar's own style, tinted with the theme text colour.</summary>
    private Control MenuIcon(string kind) => MenuIcons.Create(this, kind);
}
