using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Controls;
using Glide.App.Services;

namespace Glide.App.Settings;

/// <summary>Small inspectable editor for the user-configurable title/tab-bar utility strip.</summary>
public sealed class TitleBarCustomizerWindow : Window
{
    private readonly List<string> _shown;
    private readonly ListBox _availableList = new();
    private readonly ListBox _shownList = new();

    public TitleBarCustomizerWindow(IEnumerable<string> current)
    {
        _shown = TitleBarButtonCatalog.Normalize(current).ToList();
        Title = "Customize title-bar buttons";
        Width = 760;
        Height = 560;
        MinWidth = 680;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = ActionButton("Add →", AddSelected);
        var remove = ActionButton("← Remove", RemoveSelected);
        var up = ActionButton("Move up", () => MoveSelected(-1));
        var down = ActionButton("Move down", () => MoveSelected(1));
        var reset = ActionButton("Restore defaults", RestoreDefaults);
        var cancel = ActionButton("Cancel", () => Close((IReadOnlyList<string>?)null));
        var done = ActionButton("Done", () => Close((IReadOnlyList<string>)_shown.ToArray()), primary: true);

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 14,
            RowSpacing = 14
        };

        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "Customize title/tab-bar buttons", FontSize = 21, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Choose from 20 real Glide actions. Add, remove and reorder them; changes are saved in the same settings profile.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#AAB5C2"))
        });
        Grid.SetColumnSpan(heading, 3);
        root.Children.Add(heading);

        var availablePanel = ListPanel("Available buttons (20)", _availableList);
        Grid.SetRow(availablePanel, 1);
        root.Children.Add(availablePanel);

        var middle = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(add); middle.Children.Add(remove); middle.Children.Add(up); middle.Children.Add(down);
        Grid.SetColumn(middle, 1); Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        var shownPanel = ListPanel("Shown on title bar", _shownList);
        Grid.SetColumn(shownPanel, 2); Grid.SetRow(shownPanel, 1);
        root.Children.Add(shownPanel);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(reset);
        Grid.SetColumn(cancel, 2); footer.Children.Add(cancel);
        Grid.SetColumn(done, 3); footer.Children.Add(done);
        Grid.SetRow(footer, 2); Grid.SetColumnSpan(footer, 3);
        root.Children.Add(footer);

        Content = root;
        PopupPlacementStore.Track(this, "titlebar-customizer");
        RebuildLists();
    }

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

    private static ListBoxItem Row(TitleBarButtonDefinition definition)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*"), Margin = new Thickness(2, 3) };
        Control icon;
        if (definition.Id == "settings")
        {
            var settingsGlyph = new TextBlock
            {
                Text = "\uE713", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 19,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            };
            settingsGlyph.Classes.Add("modernTitleSettingsGlyph");
            icon = settingsGlyph;
        }
        else
        {
            icon = new GlideIconView
            {
                Kind = definition.IconKind, Width = 20, Height = 20, StrokeWidth = 1.55,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        grid.Children.Add(icon);
        var label = new TextBlock { Text = definition.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        return new ListBoxItem { Tag = definition.Id, Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void RebuildLists(string? selectShown = null)
    {
        _availableList.Items.Clear();
        foreach (var definition in TitleBarButtonCatalog.All)
            if (!_shown.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)) _availableList.Items.Add(Row(definition));

        _shownList.Items.Clear();
        foreach (var id in _shown)
            if (TitleBarButtonCatalog.Find(id) is { } definition) _shownList.Items.Add(Row(definition));

        if (selectShown is not null)
            _shownList.SelectedItem = _shownList.Items.OfType<ListBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), selectShown, StringComparison.OrdinalIgnoreCase));
    }

    private string? SelectedId(ListBox list) => (list.SelectedItem as ListBoxItem)?.Tag?.ToString();

    private void AddSelected()
    {
        var id = SelectedId(_availableList);
        if (id is null) return;
        _shown.Add(id);
        RebuildLists(id);
    }

    private void RemoveSelected()
    {
        var id = SelectedId(_shownList);
        if (id is null) return;
        _shown.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        RebuildLists();
    }

    private void MoveSelected(int delta)
    {
        var id = SelectedId(_shownList);
        if (id is null) return;
        var index = _shown.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var target = Math.Clamp(index + delta, 0, _shown.Count - 1);
        if (target == index) return;
        (_shown[index], _shown[target]) = (_shown[target], _shown[index]);
        RebuildLists(id);
    }

    private void RestoreDefaults()
    {
        _shown.Clear();
        _shown.AddRange(TitleBarButtonCatalog.DefaultButtonIds);
        RebuildLists();
    }
}
