using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.Core;

namespace Glide.App.Settings;

/// <summary>
/// status-bar customizer, built like <see cref="TitleBarCustomizerWindow"/>: "Available buttons" (hidden
/// built-in groups and every real Glide action with an icon, grouped by purpose) and "Shown on status bar", with
/// Add / Remove / Move up / Move down / Restore defaults / Cancel / Done. A click anywhere on a row selects it;
/// a double-click moves it to the other list.
/// </summary>
public sealed class StatusBarCustomizerWindow : Window
{
    private const string GroupsCategory = "Status-bar groups";
    private readonly List<string> _shown;
    private readonly ListBox _availableList = new() { SelectionMode = SelectionMode.Single, [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly ListBox _shownList = new() { SelectionMode = SelectionMode.Single, [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public StatusBarCustomizerWindow(string stored)
    {
        _shown = StatusBarGroupCatalog.Parse(stored).ToList();
        Title = "Customize status-bar buttons";
        Width = 760;
        Height = 560;
        MinWidth = 680;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 14,
            RowSpacing = 14
        };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "Customize status-bar buttons", FontSize = 21, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = $"Show, hide and reorder the built-in groups, or add any of {StatusBarGroupCatalog.AvailableActions.Count} more Glide actions as buttons. Buttons that do not fit a narrow window go into the \"...\" menu.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#AAB5C2"))
        });
        Grid.SetColumnSpan(heading, 3);
        root.Children.Add(heading);

        var availablePanel = ListPanel("Available buttons", _availableList);
        Grid.SetRow(availablePanel, 1);
        root.Children.Add(availablePanel);

        var middle = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(ActionButton("Add →", AddSelected));
        middle.Children.Add(ActionButton("← Remove", RemoveSelected));
        middle.Children.Add(ActionButton("Move up", () => MoveSelected(-1)));
        middle.Children.Add(ActionButton("Move down", () => MoveSelected(1)));
        Grid.SetColumn(middle, 1); Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        var shownPanel = ListPanel("Shown on status bar", _shownList);
        Grid.SetColumn(shownPanel, 2); Grid.SetRow(shownPanel, 1);
        root.Children.Add(shownPanel);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(ActionButton("Restore defaults", RestoreDefaults));
        var cancel = ActionButton("Cancel", () => Close((string?)null)); Grid.SetColumn(cancel, 2); footer.Children.Add(cancel);
        var done = ActionButton("Done", () => Close((string?)Result()), primary: true); Grid.SetColumn(done, 3); footer.Children.Add(done);
        Grid.SetRow(footer, 2); Grid.SetColumnSpan(footer, 3);
        root.Children.Add(footer);

        _availableList.DoubleTapped += (_, _) => AddSelected();
        _shownList.DoubleTapped += (_, _) => RemoveSelected();
        Content = root;
        PopupPlacementStore.Track(this, "statusbar-customizer");
        RebuildLists();
    }

    /// <summary>The stored value for the shown list (ids in order).</summary>
    public string Result() => StatusBarGroupCatalog.Format(_shown);

    internal IReadOnlyList<string> Shown => _shown;
    internal ListBox AvailableList => _availableList;
    internal ListBox ShownList => _shownList;

    private static Border ListPanel(string title, ListBox list)
    {
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        list.BorderThickness = new Thickness(0);
        list.Background = Brushes.Transparent;
        Grid.SetRow(list, 1); panel.Children.Add(list);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#343C42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Child = panel
        };
    }

    private static Button ActionButton(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 104, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Classes.Add(primary ? "primaryAction" : "secondaryAction");
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>A whole-row item: the row (icon, label and the empty space) is the click target, with a transparent background.</summary>
    private static ListBoxItem Row(string id)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*"), Margin = new Thickness(2, 3), Background = Brushes.Transparent };
        grid.Children.Add(new GlideIconView { Kind = StatusBarGroupCatalog.IconOf(id), Width = 20, Height = 20, StrokeWidth = 1.55, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = StatusBarGroupCatalog.LabelOf(id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, IsHitTestVisible = false };
        // The columns are narrow: a long name is trimmed with an ellipsis, and the full name is always in the tooltip.
        // The tooltip lives on the ListBoxItem, the element under the pointer anywhere on the row; the label itself is not
        // hit-testable, so it can never become a separate (nested) tooltip host.
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        return new ListBoxItem { Tag = id, Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), [ToolTip.TipProperty] = label.Text };
    }

    private static ListBoxItem Heading(string text) => CustomizerListHeading.Create(text);

    private bool IsShown(string id) => _shown.Contains(id, StringComparer.OrdinalIgnoreCase);

    private void RebuildLists(string? selectShown = null, string? selectAvailable = null)
    {
        _availableList.Items.Clear();
        var hiddenGroups = StatusBarGroupCatalog.DefaultOrder.Where(id => !IsShown(id)).ToList();
        if (hiddenGroups.Count > 0)
        {
            _availableList.Items.Add(Heading(GroupsCategory));
            foreach (var id in hiddenGroups) _availableList.Items.Add(Row(id));
        }
        foreach (var category in StatusBarGroupCatalog.ActionCategories)
        {
            var inCategory = StatusBarGroupCatalog.AvailableActions.Where(a => a.Category == category && !IsShown(a.Id)).ToList();
            if (inCategory.Count == 0) continue;
            _availableList.Items.Add(Heading(category));
            foreach (var action in inCategory) _availableList.Items.Add(Row(action.Id));
        }

        _shownList.Items.Clear();
        foreach (var id in _shown) _shownList.Items.Add(Row(id));

        if (selectShown is not null) _shownList.SelectedItem = Find(_shownList, selectShown);
        if (selectAvailable is not null) _availableList.SelectedItem = Find(_availableList, selectAvailable);
    }

    private static ListBoxItem? Find(ListBox list, string id) =>
        list.Items.OfType<ListBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), id, StringComparison.OrdinalIgnoreCase));

    private static string? SelectedId(ListBox list) => (list.SelectedItem as ListBoxItem)?.Tag?.ToString();

    internal void AddSelected()
    {
        var id = SelectedId(_availableList);
        if (id is null || IsShown(id)) return;
        _shown.Add(id);
        RebuildLists(id);
    }

    internal void RemoveSelected()
    {
        var id = SelectedId(_shownList);
        if (id is null) return;
        _shown.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        RebuildLists(selectAvailable: id);
    }

    internal void MoveSelected(int delta)
    {
        var id = SelectedId(_shownList);
        if (id is null) return;
        var index = _shown.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        var target = Math.Clamp(index + delta, 0, _shown.Count - 1);
        if (index < 0 || target == index) return;
        (_shown[index], _shown[target]) = (_shown[target], _shown[index]);
        RebuildLists(id);
    }

    internal void RestoreDefaults()
    {
        _shown.Clear();
        _shown.AddRange(StatusBarGroupCatalog.DefaultOrder);
        RebuildLists();
    }
}
