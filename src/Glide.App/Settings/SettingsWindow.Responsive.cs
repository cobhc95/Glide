using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Glide.App.Settings;

/// <summary>
/// Responsive Settings rows. Settings rows are "label | editor" grids with a fixed label column (205-300 DIP)
/// and fixed editor widths, which is right on a roomy window but pushes the editor past the card on a 720 DIP
/// wide one. When the page is narrow the label column shrinks first (the label wraps); when even that leaves
/// less than <see cref="MinSideLabel"/>, the label stacks above its editor, and a hint text that no longer fits
/// beside the editor drops under it. The gesture table does the same with its area rail and rows.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Columns of the shortcut list and its header (shared so they always line up).</summary>
    internal const string HotkeyColumns = "150,2*,*";
    private const double MinSideLabel = 120;
    private const double MinStarEditor = 140;
    private const double MinNoteBeside = 180;
    /// <summary>Card chrome between the page edge and a row: 1 px border each side plus the 14 DIP body margin each side.</summary>
    private const double CardInset = 2 + 28;
    /// <summary>Below this width of the gesture table, area names and gesture rows stack instead of sitting side by side.</summary>
    private const double GestureCompactBelow = 530;
    private const double GestureRailWide = 184;
    private const double GestureRailCompact = 148;

    private sealed class FormRow
    {
        public required Grid Grid;
        public required double LabelWidth;
        public required TextBlock Label;
        public required Thickness LabelMargin;
        public required List<(TextBlock Note, int Column, Thickness Margin)> Notes;
        public required double FixedRest;
        public bool Stacked;
        public bool NoteBelow;
        public double AppliedLabel = double.NaN;
    }

    private readonly List<FormRow> _formRows = new();
    private Grid? _gestureBody;
    private bool _gestureCompact;
    private double _responsivePageWidth = double.NaN;

    private void InitializeResponsiveLayout()
    {
        foreach (var panel in _panels.Values)
        {
            foreach (var grid in panel.GetLogicalDescendants().OfType<Grid>())
            {
                if (grid.Tag is string tag && tag.StartsWith("gestureArea:", StringComparison.Ordinal)) continue;
                var cols = grid.ColumnDefinitions;
                if (cols.Count < 2 || !cols[0].Width.IsAbsolute || grid.RowDefinitions.Count > 0) continue;
                var label = grid.Children.OfType<TextBlock>().FirstOrDefault(c => Grid.GetColumn(c) == 0 && Grid.GetRow(c) == 0);
                if (label is null) continue;
                label.TextWrapping = TextWrapping.Wrap;
                var notes = new List<(TextBlock, int, Thickness)>();
                double rest = 0;
                var starSeen = false;
                for (var c = 1; c < cols.Count; c++)
                {
                    var column = c;
                    var note = grid.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == column && t.Classes.Contains("phaseNote"));
                    if (note is not null && cols[c].Width.IsStar) { notes.Add((note, c, note.Margin)); continue; }
                    var width = cols[c].Width;
                    if (width.IsAbsolute) rest += width.Value;
                    else if (width.IsAuto)
                    {
                        foreach (var child in grid.Children.OfType<Control>().Where(ch => Grid.GetColumn(ch) == column))
                        {
                            child.Measure(Size.Infinity);
                            rest += child.DesiredSize.Width + child.Margin.Left + child.Margin.Right;
                        }
                    }
                    else if (!starSeen) { rest += MinStarEditor; starSeen = true; }
                }
                rest += grid.ColumnSpacing * (cols.Count - 1) + grid.Margin.Left + grid.Margin.Right;
                _formRows.Add(new FormRow
                {
                    Grid = grid, LabelWidth = cols[0].Width.Value, Label = label, LabelMargin = label.Margin,
                    Notes = notes, FixedRest = rest
                });
            }
        }
        ContentScroll.SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    /// <summary>Width of a page (the area inside the content margins) for the current window width.</summary>
    private double CurrentPageWidth()
    {
        var margin = ContentScroll.Content is Control page ? page.Margin : default;
        return Math.Max(0, ContentScroll.Bounds.Width - margin.Left - margin.Right);
    }

    private void ApplyResponsiveLayout()
    {
        var pageWidth = CurrentPageWidth();
        if (pageWidth <= 0) return;
        _responsivePageWidth = pageWidth;
        var available = pageWidth - CardInset;
        foreach (var row in _formRows) ApplyFormRow(row, available);
        ApplyGestureCompactLayout(force: false);
    }

    private static void ApplyFormRow(FormRow row, double available)
    {
        var cols = row.Grid.ColumnDefinitions;
        var room = available - row.FixedRest;
        var stacked = room < MinSideLabel;
        var label = stacked ? 0 : Math.Min(row.LabelWidth, room);
        // A hint beside the editor needs real room; otherwise it goes under the row.
        var beside = available - label - row.FixedRest;
        var noteBelow = row.Notes.Count > 0 && (stacked || beside < MinNoteBeside);
        if (row.Stacked == stacked && row.NoteBelow == noteBelow && Math.Abs(row.AppliedLabel - label) < 0.5) return;
        row.Stacked = stacked;
        row.NoteBelow = noteBelow;
        row.AppliedLabel = label;

        var span = cols.Count;
        var wantRows = stacked ? (noteBelow ? 3 : 2) : (noteBelow ? 2 : 0);
        if (row.Grid.RowDefinitions.Count != wantRows)
        {
            var rows = new RowDefinitions();
            for (var i = 0; i < wantRows; i++) rows.Add(new RowDefinition(GridLength.Auto));
            row.Grid.RowDefinitions = rows;
        }
        cols[0].Width = new GridLength(stacked ? 0 : label);
        Grid.SetColumnSpan(row.Label, stacked ? span : 1);
        row.Label.Margin = stacked
            ? new Thickness(row.LabelMargin.Left, row.LabelMargin.Top, row.LabelMargin.Right, row.LabelMargin.Bottom + 4)
            : row.LabelMargin;
        foreach (var child in row.Grid.Children.OfType<Control>())
        {
            if (ReferenceEquals(child, row.Label)) { Grid.SetRow(child, 0); continue; }
            var note = row.Notes.FirstOrDefault(n => ReferenceEquals(n.Note, child));
            if (note.Note is not null)
            {
                if (noteBelow)
                {
                    Grid.SetRow(child, stacked ? 2 : 1);
                    Grid.SetColumn(child, stacked ? 0 : 1);
                    Grid.SetColumnSpan(child, stacked ? span : span - 1);
                    child.Margin = new Thickness(0, 4, 0, 0);
                }
                else
                {
                    Grid.SetRow(child, 0);
                    Grid.SetColumn(child, note.Column);
                    Grid.SetColumnSpan(child, 1);
                    child.Margin = note.Margin;
                }
                continue;
            }
            Grid.SetRow(child, stacked ? 1 : 0);
        }
    }

    /// <summary>Compact gesture table: a narrower area rail and the row label above its dropdown.</summary>
    private void ApplyGestureCompactLayout(bool force)
    {
        if (_gestureBody is null) return;
        var width = double.IsNaN(_responsivePageWidth) ? CurrentPageWidth() : _responsivePageWidth;
        if (width <= 0) return;
        var compact = width - CardInset < GestureCompactBelow;
        if (!force && compact == _gestureCompact) return;
        _gestureCompact = compact;
        _gestureBody.ColumnDefinitions[0].Width = new GridLength(compact ? GestureRailCompact : GestureRailWide);
        foreach (var row in _gestureRows.Values)
        {
            if (row.Children.Count < 2) continue;
            row.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "164,*");
            row.RowDefinitions = compact ? new RowDefinitions("Auto,Auto") : new RowDefinitions();
            row.Margin = compact ? new Thickness(0, 3, 0, 5) : new Thickness(0, 1);
            Grid.SetColumn(row.Children[0], 0);
            Grid.SetRow(row.Children[0], 0);
            Grid.SetColumn(row.Children[1], compact ? 0 : 1);
            Grid.SetRow(row.Children[1], compact ? 1 : 0);
        }
    }
}
