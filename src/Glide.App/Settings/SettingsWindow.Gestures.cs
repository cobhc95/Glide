using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Settings > Mouse &amp; keyboard: the mouse gesture panel (4.8.0). A rounded card with a toolbar
/// (search, Changed only, Reset area), a left rail of areas with changed-count badges and a right
/// pane with the selected area's CLICKS / DRAGS / WHEEL rows. The area's secondary options and its
/// CUSTOM block sit full width under that table card, in the same Mouse gestures card. Every row's dropdown is a <see cref="GestureActionPicker"/> registered under
/// "gesture.&lt;slotId&gt;" so Settings search proxies the real editor.
/// </summary>
public partial class SettingsWindow
{
    private List<CustomGesture> _workingCustomGestures = new();
    private List<string> _workingRecentGestureActions = new();
    private string _gestureSelectedArea = GestureAreas.Windowed;
    private string _gestureQuery = string.Empty;
    private bool _gestureChangedOnly;
    private StackPanel? _gesturePane;
    private TextBox? _gestureSearchBox;
    private ToggleButton? _gestureChangedToggle;
    private readonly Dictionary<string, Button> _gestureAreaButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Border Badge, TextBlock Text)> _gestureBadges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _gestureRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Control>> _gestureSecondaryOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GestureActionPicker> _gestureSearchPickers = new();
    /// <summary>Selected area's OPTIONS + CUSTOM blocks, full width under the table card (not in the table).</summary>
    private StackPanel? _gestureExtrasPane;

    private const string GestureBreadcrumbRoot = "Mouse and keyboard > Gestures";

    private static string GestureDefaultText(string slotId) =>
        GestureCatalog.DefaultBehavior.TryGetValue(slotId, out var behaviour) ? $"Default ({behaviour})" : "Default";

    private string ResolveWorkingGesture(string slotId) => GestureCatalog.ResolveAction(slotId, _workingGestures);

    private bool IsGestureChanged(string slotId) => ResolveWorkingGesture(slotId) != GestureCatalog.Default;

    private GestureActionPicker NewSlotPicker(string slotId)
    {
        var picker = new GestureActionPicker(slotId, GestureDefaultText(slotId),
            () => GestureCatalog.ActionsForSlot(slotId),
            () => GestureCatalog.SuggestedForSlot(slotId),
            () => _workingRecentGestureActions);
        picker.SetAction(ResolveWorkingGesture(slotId));
        picker.ActionChosen += (p, action) => OnGestureSlotChosen(p.Id, action);
        return picker;
    }

    private void BuildGestureMatrix()
    {
        _gestureMatrixBuilt = true;
        _gestureEditors.Clear();
        _gestureRows.Clear();

        // Secondary options (modifiers of a row rather than actions) live in XAML so their load /
        // save code and Settings-search mapping are unchanged; move them into their area panes.
        foreach (var child in GestureSecondaryOptionsHost.Children.ToList())
        {
            if (child.Tag is not string tag || !tag.StartsWith("gestureArea:", StringComparison.Ordinal)) continue;
            var area = tag["gestureArea:".Length..];
            GestureSecondaryOptionsHost.Children.Remove(child);
            if (!_gestureSecondaryOptions.TryGetValue(area, out var list)) _gestureSecondaryOptions[area] = list = new List<Control>();
            if (child is Grid grid)
            {
                // Full-width option rows under the table: a flexible label column (one line when it fits,
                // wraps only if it must, never runs under the editor), a 12 px gap, and one fixed right-hand
                // editor column shared by every option so editors line up. Combos fill the column (wide
                // enough for "Smart (pan image / move window)"); number boxes stay compact.
                grid.ColumnDefinitions = new ColumnDefinitions("*,260");
                grid.ColumnSpacing = 12;
                grid.Margin = new Thickness(0);
                foreach (var label in grid.Children.OfType<TextBlock>().Where(c => Grid.GetColumn(c) == 0))
                {
                    label.TextWrapping = TextWrapping.Wrap;
                    label.TextTrimming = TextTrimming.None;
                }
                foreach (var editor in grid.Children.OfType<Control>().Where(c => Grid.GetColumn(c) == 1))
                {
                    var numeric = editor is NumericUpDown;
                    editor.Width = numeric ? 150 : double.NaN;
                    editor.HorizontalAlignment = numeric ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                    editor.VerticalAlignment = VerticalAlignment.Center;
                }
            }
            list.Add(child);
        }
        GestureSecondaryOptionsHost.IsVisible = false;

        foreach (var slot in GestureCatalog.Slots)
        {
            var picker = NewSlotPicker(slot.Id);
            _gestureEditors[slot.Id] = picker;
            _gestureRows[slot.Id] = BuildGestureRow(slot.InputLabel, GroupIcon(slot.Kind), picker, slot.Id);
            _settingControls["gesture." + slot.Id] = picker;
        }

        // Toolbar: search (fills the width), Changed only, Reset area.
        var searchHost = new Grid();
        _gestureSearchBox = new TextBox { Watermark = "Search gestures, for example \"scroll window\"" };
        _gestureSearchBox.Classes.Add("gestureSearch");
        _gestureSearchBox.TextChanged += (_, _) =>
        {
            _gestureQuery = _gestureSearchBox.Text?.Trim() ?? string.Empty;
            RenderGesturePane();
        };
        searchHost.Children.Add(_gestureSearchBox);
        var magnifier = new GlideIconView { Kind = "Search", Margin = new Thickness(9, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        magnifier.Classes.Add("gestureRowIcon");
        searchHost.Children.Add(magnifier);
        _gestureChangedToggle = new ToggleButton { Content = "Changed only" };
        _gestureChangedToggle.Classes.Add("gestureTool");
        ToolTip.SetTip(_gestureChangedToggle, "Show only rows changed from their default (and custom gestures).");
        _gestureChangedToggle.IsCheckedChanged += (_, _) =>
        {
            _gestureChangedOnly = _gestureChangedToggle.IsChecked == true;
            RenderGesturePane();
        };
        var resetArea = new Button { Content = "Reset area" };
        resetArea.Classes.Add("gestureTool");
        ToolTip.SetTip(resetArea, "Return every row of the selected area to Default and remove its custom gestures.");
        resetArea.Click += (_, _) => ResetGestureArea(_gestureSelectedArea);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 9) };
        toolbar.Children.Add(searchHost);
        Grid.SetColumn(_gestureChangedToggle, 1);
        toolbar.Children.Add(_gestureChangedToggle);
        Grid.SetColumn(resetArea, 2);
        toolbar.Children.Add(resetArea);

        // Left rail of areas.
        var rail = new StackPanel { Spacing = 2, Margin = new Thickness(6, 8, 6, 8) };
        _gestureAreaButtons.Clear();
        _gestureBadges.Clear();
        foreach (var area in GestureCatalog.Areas)
        {
            var icon = new GlideIconView { Kind = area.Icon, Width = 15, Height = 15, StrokeWidth = 1.4, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = area.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var badgeText = new TextBlock();
            var badge = new Border { Child = badgeText, IsVisible = false };
            badge.Classes.Add("gestureBadge");
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            Grid.SetColumn(badge, 2);
            content.Children.Add(badge);
            var button = new Button { Content = content, Tag = area.Id };
            button.Classes.Add("gestureArea");
            var areaId = area.Id;
            button.Click += (_, _) => SelectGestureArea(areaId);
            _gestureAreaButtons[area.Id] = button;
            _gestureBadges[area.Id] = (badge, badgeText);
            rail.Children.Add(button);
        }

        _gesturePane = new StackPanel { Spacing = 4, Margin = new Thickness(14, 10, 14, 12) };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("184,1,*"), MinHeight = 340 };
        _gestureBody = body;
        body.Children.Add(rail);
        var divider = new Border { Width = 1 };
        divider.Classes.Add("gestureHairline");
        Grid.SetColumn(divider, 1);
        body.Children.Add(divider);
        Grid.SetColumn(_gesturePane, 2);
        body.Children.Add(_gesturePane);

        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,1,*") };
        card.Children.Add(toolbar);
        var rule = new Border { Height = 1 };
        rule.Classes.Add("gestureHairline");
        Grid.SetRow(rule, 1);
        card.Children.Add(rule);
        Grid.SetRow(body, 2);
        card.Children.Add(body);
        GestureMatrixHost.Child = card;

        // The selected area's OPTIONS and CUSTOM blocks are not part of the gesture table: they sit full
        // width in the Mouse gestures card body, directly under the table card (above "Reset all gestures").
        if (_gestureExtrasPane?.Parent is Panel oldHost) oldHost.Children.Remove(_gestureExtrasPane);
        _gestureExtrasPane = new StackPanel { Name = "GestureAreaExtrasHost", Spacing = 6, Margin = new Thickness(4, 0, 0, 0) };
        if (GestureMatrixHost.Parent is Panel cardBody)
            cardBody.Children.Insert(cardBody.Children.IndexOf(GestureMatrixHost) + 1, _gestureExtrasPane);

        _searchHaystackCache.Clear();
        _searchDirectCache.Clear();
        RenderGesturePane();
        UpdateGestureBadges();
        ApplyGestureCompactLayout(force: true);
    }

    private static string GroupIcon(GestureInputKind kind) => kind switch
    {
        GestureInputKind.Drag => "Drag",
        GestureInputKind.Wheel => "Wheel",
        _ => "Click"
    };

    private static Grid BuildGestureRow(string inputLabel, string icon, Control editor, string tag)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("164,*"), Margin = new Thickness(0, 1), Tag = tag };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new GlideIconView { Kind = icon };
        glyph.Classes.Add("gestureRowIcon");
        label.Children.Add(glyph);
        var text = new TextBlock { Text = inputLabel };
        text.Classes.Add("gestureRowLabel");
        label.Children.Add(text);
        row.Children.Add(label);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private static Control GestureGroupHeader(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 8, 0, 3) };
        var label = new TextBlock { Text = text.ToUpperInvariant() };
        label.Classes.Add("gestureGroupHeader");
        grid.Children.Add(label);
        var rule = new Border { Height = 1, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        rule.Classes.Add("gestureHairline");
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        return grid;
    }

    private static TextBlock GestureHint(string text)
    {
        var hint = new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 0) };
        hint.Classes.Add("gestureHint");
        return hint;
    }

    private void SelectGestureArea(string area)
    {
        _gestureSelectedArea = area;
        if (_gestureSearchBox is not null && !string.IsNullOrEmpty(_gestureSearchBox.Text))
        {
            _gestureQuery = string.Empty;
            _gestureSearchBox.Text = string.Empty;
        }
        RenderGesturePane();
    }

    private string GestureRowHaystack(GestureSlotDefinition slot)
    {
        var action = ResolveWorkingGesture(slot.Id);
        var setting = Glide.Core.Settings.SettingsCatalog.All.FirstOrDefault(s => s.Id == "gesture." + slot.Id);
        return string.Join(' ', new[]
        {
            GestureCatalog.AreaLabel(slot.Area), slot.InputLabel, GestureDefaultText(slot.Id),
            GestureCatalog.FindAction(action)?.Label ?? string.Empty
        }.Concat(setting?.SearchTerms ?? []));
    }

    private void RenderGesturePane()
    {
        if (_gesturePane is null) return;
        _gesturePane.Children.Clear();
        // Area options + custom gestures (under the table card); hidden while the table shows search results.
        var extras = _gestureExtrasPane ?? _gesturePane;
        if (_gestureExtrasPane is not null)
        {
            _gestureExtrasPane.Children.Clear();
            _gestureExtrasPane.IsVisible = _gestureQuery.Length == 0;
        }
        foreach (var pair in _gestureAreaButtons)
            pair.Value.Classes.Set("selected", _gestureQuery.Length == 0 && string.Equals(pair.Key, _gestureSelectedArea, StringComparison.OrdinalIgnoreCase));

        if (_gestureQuery.Length > 0)
        {
            var words = _gestureQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var any = false;
            foreach (var area in GestureCatalog.Areas)
            {
                var rows = GestureCatalog.SlotsForArea(area.Id)
                    .Where(s => (!_gestureChangedOnly || IsGestureChanged(s.Id)) &&
                                words.All(w => GestureRowHaystack(s).Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var customs = CustomGestureSearchMatches(words).Where(i => _workingCustomGestures[i].Area == area.Id).ToList();
                if (rows.Count == 0 && customs.Count == 0) continue;
                any = true;
                _gesturePane.Children.Add(GestureGroupHeader(area.Label));
                foreach (var slot in rows) _gesturePane.Children.Add(_gestureRows[slot.Id]);
                foreach (var index in customs) _gesturePane.Children.Add(BuildCustomRow(index));
            }
            if (!any) _gesturePane.Children.Add(GestureHint("No gesture matches. Try an area, an input (wheel, middle click) or an action."));
            return;
        }

        var selected = _gestureSelectedArea;
        var slots = GestureCatalog.SlotsForArea(selected).Where(s => !_gestureChangedOnly || IsGestureChanged(s.Id)).ToList();
        void Group(string title, Func<GestureSlotDefinition, bool> filter)
        {
            var members = slots.Where(filter).ToList();
            if (members.Count == 0) return;
            _gesturePane!.Children.Add(GestureGroupHeader(title));
            foreach (var slot in members) _gesturePane.Children.Add(_gestureRows[slot.Id]);
        }
        Group("Clicks", s => s.Kind is GestureInputKind.Click or GestureInputKind.DoubleClick);
        Group("Drags", s => s.Kind == GestureInputKind.Drag);
        Group("Wheel", s => s.Kind == GestureInputKind.Wheel);
        if (!_gestureChangedOnly && _gestureSecondaryOptions.TryGetValue(selected, out var options) && options.Count > 0)
        {
            extras.Children.Add(GestureGroupHeader(selected == GestureAreas.TitleBar ? "Caption buttons" : "Options"));
            foreach (var option in options) extras.Children.Add(option);
        }

        extras.Children.Add(GestureGroupHeader("Custom"));
        for (var i = 0; i < _workingCustomGestures.Count; i++)
            if (_workingCustomGestures[i].Area == selected) extras.Children.Add(BuildCustomRow(i));
        var count = _workingCustomGestures.Count(c => c.Area == selected);
        var full = count >= GestureCatalog.MaxCustomGesturesPerArea;
        var dashed = new Rectangle
        {
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            RadiusX = 7,
            RadiusY = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        dashed.Bind(Shape.StrokeProperty, this.GetResourceObservable("BrushInputBorder"));
        var addContent = new Grid { Height = 29 };
        addContent.Children.Add(dashed);
        addContent.Children.Add(new TextBlock
        {
            Text = full ? $"Maximum {GestureCatalog.MaxCustomGesturesPerArea} custom gestures in this area" : "+  Add custom gesture",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        var add = new Button { Content = addContent, IsEnabled = !full, Margin = new Thickness(0, 3, 0, 0) };
        add.Classes.Add("gestureAddCustom");
        add.Click += (_, _) => AddCustomGesture(selected);
        extras.Children.Add(add);
        extras.Children.Add(GestureHint("Pick an input, an optional key to hold, and what it does."));
    }

    private Control BuildCustomRow(int index)
    {
        var custom = _workingCustomGestures[index];
        var inputs = GestureCatalog.CustomInputsForArea(custom.Area);
        var inputCombo = new ComboBox();
        inputCombo.Classes.Add("gestureCustomCombo");
        foreach (var input in inputs) inputCombo.Items.Add(new ComboBoxItem { Content = GestureInputs.BaseLabel(input), Tag = input });
        inputCombo.SelectedIndex = Math.Max(0, Array.IndexOf(inputs.ToArray(), custom.Input));
        ToolTip.SetTip(inputCombo, "Input");
        var holdCombo = new ComboBox();
        holdCombo.Classes.Add("gestureCustomCombo");
        foreach (var modifier in GestureModifiers.All)
            holdCombo.Items.Add(new ComboBoxItem { Content = modifier == GestureModifiers.None ? "Hold: none" : "Hold " + GestureModifiers.Label(modifier), Tag = modifier });
        holdCombo.SelectedIndex = Math.Max(0, Array.IndexOf(GestureModifiers.All, GestureModifiers.Normalize(custom.Modifier)));
        ToolTip.SetTip(holdCombo, "Key to hold (optional)");
        // A custom gesture is a user choice: its input and any held key use the accent "changed" field look.
        inputCombo.Classes.Add("changed");
        holdCombo.Classes.Set("changed", GestureModifiers.Normalize(custom.Modifier) != GestureModifiers.None);
        var picker = new GestureActionPicker("custom:" + index, null,
            () => GestureCatalog.ActionsForCustom(custom.Area, custom.Input),
            () => GestureCatalog.Slots.FirstOrDefault(s => s.Area == custom.Area && s.Input == custom.Input) is { } slot ? GestureCatalog.SuggestedForSlot(slot.Id) : [],
            () => _workingRecentGestureActions);
        picker.SetAction(custom.Action);
        picker.ActionChosen += (_, action) =>
        {
            custom.Action = action;
            AddRecentGestureAction(action);
            AfterCustomGestureChanged(rerender: false);
        };
        inputCombo.SelectionChanged += (_, _) =>
        {
            if (inputCombo.SelectedItem is not ComboBoxItem { Tag: string input } || input == custom.Input) return;
            custom.Input = input;
            if (!GestureCatalog.ActionsForCustom(custom.Area, input).Any(a => a.Id == custom.Action)) custom.Action = GestureCatalog.Nothing;
            AfterCustomGestureChanged(rerender: true);
        };
        holdCombo.SelectionChanged += (_, _) =>
        {
            if (holdCombo.SelectedItem is not ComboBoxItem { Tag: string modifier } || modifier == custom.Modifier) return;
            custom.Modifier = modifier;
            holdCombo.Classes.Set("changed", GestureModifiers.Normalize(modifier) != GestureModifiers.None);
            AfterCustomGestureChanged(rerender: false);
        };
        var delete = new Button { Content = new GlideIconView { Kind = "Close", Width = 11, Height = 11, StrokeWidth = 1.4 } };
        delete.Classes.Add("gestureIconButton");
        ToolTip.SetTip(delete, "Remove this custom gesture");
        delete.Click += (_, _) =>
        {
            _workingCustomGestures.Remove(custom);
            AfterCustomGestureChanged(rerender: true);
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,0.8*,1.6*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 1), Tag = "custom:" + index };
        row.Children.Add(inputCombo);
        Grid.SetColumn(holdCombo, 1);
        row.Children.Add(holdCombo);
        Grid.SetColumn(picker, 2);
        row.Children.Add(picker);
        Grid.SetColumn(delete, 3);
        row.Children.Add(delete);
        return row;
    }

    private void AddCustomGesture(string area)
    {
        if (_workingCustomGestures.Count(c => c.Area == area) >= GestureCatalog.MaxCustomGesturesPerArea) return;
        var inputs = GestureCatalog.CustomInputsForArea(area);
        if (inputs.Count == 0) return;
        var input = inputs.Contains(GestureInputs.MiddleClick) ? GestureInputs.MiddleClick : inputs[0];
        _workingCustomGestures.Add(new CustomGesture { Area = area, Input = input, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.Nothing });
        AfterCustomGestureChanged(rerender: true);
    }

    private void AfterCustomGestureChanged(bool rerender)
    {
        if (rerender) RenderGesturePane();
        UpdateGestureBadges();
        PreviewGestures();
    }

    private void AddRecentGestureAction(string actionId)
    {
        if (actionId is GestureCatalog.Default or GestureCatalog.Nothing) return;
        _workingRecentGestureActions.RemoveAll(x => string.Equals(x, actionId, StringComparison.OrdinalIgnoreCase));
        _workingRecentGestureActions.Insert(0, actionId);
        if (_workingRecentGestureActions.Count > 6) _workingRecentGestureActions.RemoveRange(6, _workingRecentGestureActions.Count - 6);
    }

    private void OnGestureSlotChosen(string slotId, string actionId)
    {
        if (_loadingControls) return;
        _workingGestures[slotId] = actionId;
        AddRecentGestureAction(actionId);
        SyncGesturePickers(slotId);
        UpdateGestureBadges();
        if (_gestureChangedOnly) RenderGesturePane();
        PreviewGestures();
    }

    private void SyncGesturePickers(string? slotId = null)
    {
        foreach (var picker in _gestureEditors.Values.Concat(_gestureSearchPickers))
            if (slotId is null || string.Equals(picker.Id, slotId, StringComparison.OrdinalIgnoreCase))
                picker.SetAction(ResolveWorkingGesture(picker.Id));
    }

    /// <summary>
    /// Tooltips of other settings that mention a mouse gesture ("right-click the tab/title bar") follow the gesture rows
    /// being edited: the hint is dropped while that gesture no longer opens the menu.
    /// </summary>
    private void UpdateGestureDependentTips()
    {
        var menuOnRightClick = BindingHints.GestureRuns("titleBar.rightClick", GestureCatalog.ContextMenu, _workingGestures, _workingCustomGestures, defaultRunsAction: true);
        ToolTip.SetTip(FullscreenKeepTabBarOpenCheck, menuOnRightClick ? "Can also be toggled by right-clicking the tab/title bar while fullscreen." : null);
    }

    private void PreviewGestures()
    {
        UpdateGestureDependentTips();
        if (_loadingControls) return;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private void UpdateGestureBadges()
    {
        foreach (var area in GestureCatalog.Areas)
        {
            if (!_gestureBadges.TryGetValue(area.Id, out var badge)) continue;
            var count = GestureCatalog.SlotsForArea(area.Id).Count(s => IsGestureChanged(s.Id)) +
                        _workingCustomGestures.Count(c => c.Area == area.Id);
            badge.Badge.IsVisible = count > 0;
            badge.Text.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ToolTip.SetTip(_gestureAreaButtons[area.Id], count == 0 ? $"{area.Label}: all rows at default" : $"{area.Label}: {count} changed");
        }
    }

    /// <summary>Re-reads the working gesture state into the panel (after load, import, preset or reset).</summary>
    private void RefreshGesturePanel()
    {
        if (!_gestureMatrixBuilt) return;
        SyncGesturePickers();
        RenderGesturePane();
        UpdateGestureBadges();
    }

    private void LoadGestureEditorsFromWorkingMap() => RefreshGesturePanel();

    private void ResetGestureArea(string area)
    {
        foreach (var slot in GestureCatalog.SlotsForArea(area)) _workingGestures[slot.Id] = GestureCatalog.Default;
        _workingCustomGestures.RemoveAll(c => c.Area == area);
        RefreshGesturePanel();
        PreviewGestures();
    }

    private void ResetGestureMatrixClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _workingGestures = GestureCatalog.CreateDefaultMap();
        _workingCustomGestures.Clear();
        RefreshGesturePanel();
        PreviewGestures();
    }

    /// <summary>Search "Open": switch to the Mouse page, select the row's area and bring the row into view.</summary>
    private void OpenGestureRow(string slotId)
    {
        ShowCategory("Mouse", clearSearch: true);
        if (!_gestureMatrixBuilt) { BuildGestureMatrix(); LoadGestureEditorsFromWorkingMap(); }
        var slot = GestureCatalog.FindSlot(slotId);
        OpenGestureArea(slot?.Area ?? _gestureSelectedArea, slot is null ? null : _gestureRows.GetValueOrDefault(slot.Id));
    }

    private void OpenGestureArea(string area, Control? target = null)
    {
        ShowCategory("Mouse", clearSearch: true);
        if (_cardById.TryGetValue("mouse-gestures", out var gestureCard)) gestureCard.IsExpanded = true;
        if (!_gestureMatrixBuilt) { BuildGestureMatrix(); LoadGestureEditorsFromWorkingMap(); }
        _gestureChangedOnly = false;
        if (_gestureChangedToggle is not null) _gestureChangedToggle.IsChecked = false;
        SelectGestureArea(area);
        var bring = target ?? (Control?)GestureMatrixHost;
        Dispatcher.UIThread.Post(() => bring?.BringIntoView(), DispatcherPriority.Background);
    }

    private static Border AreaPill(string area)
    {
        var pill = new Border { Child = new TextBlock { Text = GestureCatalog.AreaLabel(area) } };
        pill.Classes.Add("gesturePill");
        return pill;
    }

    private Control GestureSearchCard(string hiddenLabel, string area, string title, string breadcrumb, Control editor, IEnumerable<Button> companions, string tip)
    {
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        // The catalogue label is kept as a (hidden) text element so search tests and assistive
        // tooling can identify the card; the visible title is the area pill + the input name.
        text.Children.Add(new TextBlock { Text = hiddenLabel, IsVisible = false });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        titleRow.Children.Add(AreaPill(area));
        titleRow.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock { Text = breadcrumb, Foreground = SearchMutedBrush(), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        editor.Width = 240;
        var right = BuildSearchEditorGroup(editor, companions.ToList());
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        border.Child = grid;
        ToolTip.SetTip(border, tip);
        return border;
    }

    private Control BuildGestureSearchCard(Glide.Core.Settings.SettingDefinition setting, GestureSlotDefinition slot)
    {
        var picker = NewSlotPicker(slot.Id);
        _gestureSearchPickers.Add(picker);
        var area = GestureCatalog.AreaLabel(slot.Area);
        return GestureSearchCard(setting.Label, slot.Area, slot.InputLabel, $"{GestureBreadcrumbRoot} > {area} > {slot.InputLabel}",
            picker, CreateSearchCompanionButtons(setting.Id), setting.Description);
    }

    private static string CustomGestureTitle(CustomGesture custom)
    {
        var mod = GestureModifiers.Normalize(custom.Modifier);
        return "Custom: " + GestureInputs.BaseLabel(custom.Input) + (mod == GestureModifiers.None ? string.Empty : " + " + GestureModifiers.Label(mod));
    }

    private IEnumerable<int> CustomGestureSearchMatches(IReadOnlyList<string> words)
    {
        for (var i = 0; i < _workingCustomGestures.Count; i++)
        {
            var custom = _workingCustomGestures[i];
            var haystack = string.Join(' ', "custom gesture mouse hold", GestureCatalog.AreaLabel(custom.Area), GestureInputs.BaseLabel(custom.Input),
                GestureInputs.KindOf(custom.Input) == GestureInputKind.Wheel ? "wheel scroll" : "click", custom.Input is GestureInputs.BackButton or GestureInputs.ForwardButton ? "side button xbutton1 xbutton2 mouse 4 mouse 5" : string.Empty, GestureModifiers.Label(custom.Modifier),
                GestureCatalog.FindAction(custom.Action)?.Label ?? custom.Action);
            if (words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase))) yield return i;
        }
    }

    private Control BuildCustomGestureSearchCard(int index)
    {
        var custom = _workingCustomGestures[index];
        var picker = new GestureActionPicker("custom:" + index, null,
            () => GestureCatalog.ActionsForCustom(custom.Area, custom.Input),
            () => GestureCatalog.Slots.FirstOrDefault(s => s.Area == custom.Area && s.Input == custom.Input) is { } slot ? GestureCatalog.SuggestedForSlot(slot.Id) : [],
            () => _workingRecentGestureActions);
        picker.SetAction(custom.Action);
        picker.ActionChosen += (_, action) =>
        {
            custom.Action = action;
            AddRecentGestureAction(action);
            AfterCustomGestureChanged(rerender: true);
        };
        var open = new Button { Content = "Open", MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        open.Classes.Add("secondaryAction");
        open.Click += (_, _) => OpenGestureArea(custom.Area);
        var area = GestureCatalog.AreaLabel(custom.Area);
        var title = CustomGestureTitle(custom);
        return GestureSearchCard(title, custom.Area, title, $"{GestureBreadcrumbRoot} > {area} > Custom", picker, [open],
            "A custom gesture overrides the built-in row for the same input and held key.");
    }
}
