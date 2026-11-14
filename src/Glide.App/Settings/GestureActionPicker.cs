using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// The dropdown of one gesture row ("design C, compact"). Shows muted "Default (...)" while
/// unchanged, or an accent outline + dot and the action name when changed. Opens a small popup with
/// a "Find an action" box, Default / Do nothing, Suggested and Recent chips and an "All actions"
/// drill-down grouped by category. It only ever offers the actions valid for its input.
/// </summary>
internal sealed class GestureActionPicker : Button
{
    private readonly Func<IReadOnlyList<GestureActionDefinition>> _validActions;
    private readonly Func<IReadOnlyList<GestureActionDefinition>> _suggested;
    private readonly Func<IReadOnlyList<string>> _recent;
    private readonly string? _defaultText;
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Ellipse _dot;
    private Flyout? _flyout;
    private string _actionId = GestureCatalog.Default;

    public GestureActionPicker(string id, string? defaultText,
        Func<IReadOnlyList<GestureActionDefinition>> validActions,
        Func<IReadOnlyList<GestureActionDefinition>> suggested,
        Func<IReadOnlyList<string>> recent)
    {
        Id = id;
        _defaultText = defaultText;
        _validActions = validActions;
        _suggested = suggested;
        _recent = recent;
        Classes.Add("gesturePicker");
        _dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _dot.Classes.Add("gestureDot");
        _dot.Bind(Shape.FillProperty, this.GetResourceObservable("BrushAccent"));
        _text.Classes.Add("gesturePickerText");
        // "Expand" is an up chevron; the style rotates it 180 degrees (down) while the popup is closed.
        // Styles reach it only because StyleKeyOverride makes this control match "Button" selectors.
        var chevron = new Glide.App.Controls.GlideIconView { Kind = "Expand", Width = 15, Height = 15, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chevron.Classes.Add("gesturePickerChevron");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(_dot);
        Grid.SetColumn(_text, 1);
        grid.Children.Add(_text);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        Content = grid;
        Click += (_, _) => OpenPopup();
        Render();
    }

    /// <summary>
    /// Style as a plain Button. Avalonia's type selectors match the exact style key, so without this
    /// every "Button.gesturePicker" style in GlideTheme.axaml silently skipped this subclass (the Fluent
    /// Button template still applied, so it rendered as bare text with an unrotated, up-pointing chevron).
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Slot id, or "custom:&lt;n&gt;" for a custom gesture row.</summary>
    public string Id { get; }
    public string ActionId => _actionId;
    public string DisplayText => _text.Text ?? string.Empty;
    public bool ShowsChangedDot => _dot.IsVisible;
    public event Action<GestureActionPicker, string>? ActionChosen;

    public void SetAction(string? actionId)
    {
        _actionId = GestureCatalog.NormalizeAction(actionId);
        Render();
    }

    private void Render()
    {
        var isDefault = _actionId == GestureCatalog.Default;
        _text.Text = isDefault && _defaultText is not null ? _defaultText : LabelOf(_actionId);
        Classes.Set("changed", !isDefault);
        // The dot is driven here, not only by a style, so it can never show on a row at its default.
        _dot.IsVisible = !isDefault;
        ToolTip.SetTip(this, isDefault ? "Unchanged: " + _text.Text : "Changed from default: " + _text.Text);
    }

    private static string LabelOf(string actionId) => GestureCatalog.FindAction(actionId)?.Label ?? actionId;

    private void Choose(string actionId)
    {
        _flyout?.Hide();
        if (string.Equals(actionId, _actionId, StringComparison.OrdinalIgnoreCase)) return;
        SetAction(actionId);
        ActionChosen?.Invoke(this, _actionId);
    }

    private void OpenPopup()
    {
        var search = new TextBox { Watermark = "Find an action", FontSize = 12.5, MinHeight = 27, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(6) };
        search.Classes.Add("gestureSearch");
        search.Padding = new Thickness(8, 3);
        var body = new ContentControl();
        var root = new StackPanel { Width = 212, Spacing = 6 };
        root.Children.Add(search);
        root.Children.Add(body);
        void ShowHome() => body.Content = BuildHome(ShowAll);
        void ShowAll() => body.Content = BuildAll(ShowHome);
        search.TextChanged += (_, _) =>
        {
            var q = search.Text?.Trim() ?? string.Empty;
            if (q.Length == 0) ShowHome(); else body.Content = BuildMatches(q);
        };
        ShowHome();
        _flyout = new Flyout { Content = root, Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
        _flyout.FlyoutPresenterClasses.Add("gesturePickerFlyout");
        _flyout.Opened += (_, _) => { Classes.Set("open", true); search.Focus(); };
        _flyout.Closed += (_, _) => Classes.Set("open", false);
        _flyout.ShowAt(this);
    }

    private Button Item(string text, string actionId, string? tag = null, bool muted = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (muted) label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushMuted"));
        grid.Children.Add(label);
        if (tag is not null)
        {
            var t = new TextBlock { Text = tag, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushMuted2"));
            Grid.SetColumn(t, 1);
            grid.Children.Add(t);
        }
        var button = new Button { Content = grid, Tag = actionId };
        button.Classes.Add("gesturePickItem");
        if (string.Equals(actionId, _actionId, StringComparison.OrdinalIgnoreCase)) button.Classes.Add("current");
        button.Click += (_, _) => Choose(actionId);
        return button;
    }

    private Button Chip(GestureActionDefinition action)
    {
        var chip = new Button { Content = action.Label, Tag = action.Id };
        chip.Classes.Add("gestureChip");
        if (string.Equals(action.Id, _actionId, StringComparison.OrdinalIgnoreCase)) chip.Classes.Add("current");
        chip.Click += (_, _) => Choose(action.Id);
        return chip;
    }

    private static TextBlock Header(string text)
    {
        var header = new TextBlock { Text = text.ToUpperInvariant(), Margin = new Thickness(2, 4, 0, 1) };
        header.Classes.Add("gestureGroupHeader");
        return header;
    }

    private Control BuildHome(Action showAll)
    {
        var valid = _validActions();
        var panel = new StackPanel { Spacing = 2 };
        if (_defaultText is not null) panel.Children.Add(Item(_defaultText, GestureCatalog.Default, muted: true));
        if (valid.Any(a => a.Id == GestureCatalog.Nothing)) panel.Children.Add(Item("Do nothing", GestureCatalog.Nothing));
        var suggested = _suggested().Where(a => valid.Contains(a)).ToList();
        if (suggested.Count > 0)
        {
            panel.Children.Add(Header("Suggested"));
            var wrap = new WrapPanel();
            foreach (var action in suggested) wrap.Children.Add(Chip(action));
            panel.Children.Add(wrap);
        }
        var recent = _recent().Select(id => valid.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(a => a is not null && a.Id is not (GestureCatalog.Default or GestureCatalog.Nothing) && !suggested.Contains(a)).Take(4).ToList();
        if (recent.Count > 0)
        {
            panel.Children.Add(Header("Recent"));
            var wrap = new WrapPanel();
            foreach (var action in recent) wrap.Children.Add(Chip(action!));
            panel.Children.Add(wrap);
        }
        // A binding kept from before 4.8.0 that this row no longer offers stays visible and selected.
        if (_actionId != GestureCatalog.Default && valid.All(a => a.Id != _actionId))
            panel.Children.Add(Item(LabelOf(_actionId) + " (kept)", _actionId, "earlier"));
        var all = new Button { Tag = "all" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = "All actions", VerticalAlignment = VerticalAlignment.Center });
        var arrow = new TextBlock { Text = "›", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        all.Content = grid;
        all.Classes.Add("gesturePickItem");
        all.Margin = new Thickness(0, 4, 0, 0);
        all.Click += (_, _) => showAll();
        panel.Children.Add(all);
        return panel;
    }

    private IEnumerable<GestureActionDefinition> ListableActions() =>
        _validActions().Where(a => a.Id is not (GestureCatalog.Default or GestureCatalog.Nothing));

    private Control BuildAll(Action back)
    {
        var panel = new StackPanel { Spacing = 1 };
        var header = new Button { Tag = "back" };
        header.Classes.Add("gesturePickItem");
        header.Content = new TextBlock { Text = "‹  All actions", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Click += (_, _) => back();
        var list = new StackPanel { Spacing = 1 };
        foreach (var group in ListableActions().GroupBy(a => a.Category)
                     .OrderBy(g => { var i = Array.IndexOf(GestureActionCategories.Order, g.Key); return i < 0 ? 99 : i; }))
        {
            list.Children.Add(Header(group.Key));
            foreach (var action in group) list.Children.Add(Item(action.Label, action.Id));
        }
        var root = new StackPanel { Spacing = 2 };
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    private Control BuildMatches(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _validActions()
            .Where(a => words.All(w => a.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Category.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       (a.Id == GestureCatalog.Default && _defaultText is not null && _defaultText.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var list = new StackPanel { Spacing = 1 };
        foreach (var action in matches)
        {
            var text = action.Id == GestureCatalog.Default ? _defaultText ?? action.Label : action.Label;
            list.Children.Add(Item(text, action.Id, action.Id == GestureCatalog.Default ? null : action.Category));
        }
        var root = new StackPanel { Spacing = 2 };
        var count = new TextBlock { Text = matches.Count == 1 ? "1 match" : $"{matches.Count} matches", Margin = new Thickness(2, 0, 0, 2) };
        count.Classes.Add("gestureHint");
        root.Children.Add(count);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }
}
