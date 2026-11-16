using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>
/// The one tick-box list of Glide data categories, built from <see cref="GlideDataCategoryCatalog"/>: used by the Reset Glide
/// card, the Back up and restore card and the backup import dialog, so all three show the same groups, order, titles and
/// descriptions. Layout (approved Reset Glide mockup): an upper-case group label with a rule, the group's tick boxes in two
/// equal columns (title + one-line description), then a bar with "Select all", "Clear", the "N selected" count and the
/// owner's action buttons right-aligned.
/// Control names start with <paramref name="namePrefix"/>: <c>{prefix}{Id}Check</c>, <c>{prefix}SelectAllButton</c>,
/// <c>{prefix}ClearButton</c>, <c>{prefix}CountText</c>, <c>{prefix}Bar</c>.
/// </summary>
internal sealed class DataCategorySelector : StackPanel
{
    private readonly Dictionary<ResetItemId, CheckBox> _checks = new();
    private readonly List<GlideDataCategory> _categories;
    private readonly StackPanel _actions;

    public DataCategorySelector(string namePrefix, GlideDataCapabilities capability, IEnumerable<GlideDataCategory> categories)
    {
        Capability = capability;
        _categories = categories.ToList();
        Spacing = 0;

        var first = true;
        foreach (var group in _categories.GroupBy(x => x.Group))
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, first ? 0 : 12, 0, 0) };
            var label = new TextBlock { Text = GlideDataCategoryCatalog.GroupTitle(group.Key).ToUpperInvariant() };
            label.Classes.Add("resetGroupLabel");
            var rule = new Border();
            rule.Classes.Add("resetGroupRule");
            Grid.SetColumn(rule, 1);
            header.Children.Add(label);
            header.Children.Add(rule);
            Children.Add(header);
            first = false;

            var grid = new UniformGrid { Columns = 2, RowSpacing = 2, ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var category in group)
            {
                var check = new CheckBox { Name = namePrefix + category.Id + "Check", Tag = category.SettingIdFor(capability) };
                check.Classes.Add("resetItem");
                var text = new StackPanel();
                var title = new TextBlock { Text = category.Title };
                title.Classes.Add("resetItemTitle");
                var hint = new TextBlock { Text = category.DescriptionFor(capability), Margin = new Thickness(0, 1, 0, 0) };
                hint.Classes.Add("resetItemHint");
                text.Children.Add(title);
                text.Children.Add(hint);
                check.Content = text;
                // The label is a panel (title + hint), so give assistive technology the plain title and the hint as help text.
                AutomationProperties.SetName(check, category.Title);
                AutomationProperties.SetHelpText(check, category.DescriptionFor(capability));
                check.IsCheckedChanged += (_, _) => Update();
                _checks[category.Id] = check;
                grid.Children.Add(check);
            }
            Children.Add(grid);
        }

        SelectAllButton = new Button { Name = namePrefix + "SelectAllButton", Content = "Select all" };
        SelectAllButton.Classes.Add("secondaryAction");
        SelectAllButton.Classes.Add("resetBar");
        SelectAllButton.Click += (_, _) => SetAll(true);
        ClearButton = new Button { Name = namePrefix + "ClearButton", Content = "Clear" };
        ClearButton.Classes.Add("secondaryAction");
        ClearButton.Classes.Add("resetBar");
        ClearButton.Click += (_, _) => SetAll(false);
        CountText = new TextBlock
        {
            Name = namePrefix + "CountText", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        };
        CountText.Bind(TextBlock.ForegroundProperty, CountText.GetResourceObservable("BrushMuted"));
        _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        Bar = new Grid { Name = namePrefix + "Bar", ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(ClearButton, 1);
        Grid.SetColumn(CountText, 2);
        Grid.SetColumn(_actions, 4);
        Bar.Children.Add(SelectAllButton);
        Bar.Children.Add(ClearButton);
        Bar.Children.Add(CountText);
        Bar.Children.Add(_actions);
        var barRule = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = Bar };
        barRule.Classes.Add("resetBarRule");
        Children.Add(barRule);
        Update();
    }

    /// <summary>Which card's wording and Settings-search ids the tick boxes use.</summary>
    public GlideDataCapabilities Capability { get; }

    public Button SelectAllButton { get; }
    public Button ClearButton { get; }
    public TextBlock CountText { get; }
    public Grid Bar { get; }

    /// <summary>The categories shown, in catalogue order.</summary>
    public IReadOnlyList<GlideDataCategory> Categories => _categories;

    /// <summary>The tick box of each category (the Settings-search map registers these).</summary>
    public IReadOnlyDictionary<ResetItemId, CheckBox> Checks => _checks;

    /// <summary>The first tick box (keyboard focus target when the card is opened from elsewhere).</summary>
    public CheckBox? FirstCheck => _categories.Count == 0 ? null : _checks[_categories[0].Id];

    /// <summary>Raised whenever the ticked set changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>The ticked categories in catalogue order.</summary>
    public IReadOnlyList<ResetItemId> Selected => _categories.Where(x => _checks[x.Id].IsChecked == true).Select(x => x.Id).ToArray();

    /// <summary>Adds an owner button to the right end of the bar (in call order).</summary>
    public void AddAction(Button button) => _actions.Children.Add(button);

    public void SetAll(bool value)
    {
        foreach (var check in _checks.Values) check.IsChecked = value;
        Update();
    }

    /// <summary>Text of the count: "Nothing selected" or "N selected".</summary>
    public static string CountLabel(int count) => count == 0 ? "Nothing selected" : $"{count} selected";

    /// <summary>Refreshes the count (the owner can override it while an operation runs).</summary>
    public void Update()
    {
        CountText.Text = CountLabel(Selected.Count);
        SelectionChanged?.Invoke();
    }
}
