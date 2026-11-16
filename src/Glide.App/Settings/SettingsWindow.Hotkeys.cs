using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glide.App.Services;
using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Keyboard shortcuts: the shortcut list, filter, presets and shortcut capture.
/// Split from SettingsWindow.axaml.cs; behaviour unchanged.
/// </summary>
public partial class SettingsWindow
{
    private DispatcherTimer? _hotkeyFilterTimer;

    private void HotkeyFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingControls) return;
        // Typing in the filter leaves a "Search hotkey" result view.
        if (_hotkeyLookupShortcut is not null) ClearHotkeyLookup(rebuild: false);
        UpdateHotkeyFilterChrome();
        // Short debounce so typing never rebuilds the list on every keystroke.
        _hotkeyFilterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hotkeyFilterTimer.Stop();
        _hotkeyFilterTimer.Tick -= HotkeyFilterTick;
        _hotkeyFilterTimer.Tick += HotkeyFilterTick;
        _hotkeyFilterTimer.Start();
    }

    private void HotkeyFilterTick(object? sender, EventArgs e)
    {
        _hotkeyFilterTimer?.Stop();
        BuildHotkeyRows(HotkeyFilterBox?.Text);
    }

    private void BuildHotkeyRows(string? filter = null)
    {
        _hotkeyRowsBuilt = true;
        UpdateExternalProgramHotkeyLabels();
        if (HotkeyList is null) return;
        var words = (filter ?? string.Empty).Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
        var data = new List<HotkeyListRow>();
        foreach (var action in HotkeyCatalog.All)
        {
            var shortcuts = _workingHotkeys.TryGetValue(action.Id, out var values) ? values : new List<string>();
            if (_hotkeyLookupShortcut is { } looked)
            {
                // "Search hotkey" result view: only the action(s) that own exactly that key.
                var owned = shortcuts.FirstOrDefault(s => string.Equals(s, looked, StringComparison.OrdinalIgnoreCase));
                if (owned is not null) data.Add(new HotkeyListRow(action.Id, owned, action.Label, action.Category));
                continue;
            }
            if (words.Length > 0)
            {
                var haystack = action.Label + " " + action.Category + " " + string.Join(" ", shortcuts) + " " + string.Join(" ", action.SearchTerms ?? Array.Empty<string>());
                if (!words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            }

            if (shortcuts.Count == 0) data.Add(new HotkeyListRow(action.Id, "", action.Label, action.Category));
            else foreach (var shortcut in shortcuts) data.Add(new HotkeyListRow(action.Id, shortcut, action.Label, action.Category));
        }
        // Fixed shortcuts (Explorer list keys, slideshow Space...) are listed read-only after the editable rows so
        // the list shows every key Glide reacts to. They come from the master action registry.
        foreach (var fixedShortcut in HotkeyCatalog.FixedShortcuts)
        {
            if (_hotkeyLookupShortcut is { } looked)
            {
                if (HotkeyLookup.FixedOwners(looked).Any(f => f.Id == fixedShortcut.Id))
                    data.Add(new HotkeyListRow(fixedShortcut.Id, fixedShortcut.Shortcut, fixedShortcut.Label, fixedShortcut.Category));
                continue;
            }
            if (words.Length > 0)
            {
                var haystack = fixedShortcut.Label + " " + fixedShortcut.Category + " " + fixedShortcut.Shortcut + " " + fixedShortcut.Description + " " + string.Join(" ", fixedShortcut.SearchTerms);
                if (!words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            }
            data.Add(new HotkeyListRow(fixedShortcut.Id, fixedShortcut.Shortcut, fixedShortcut.Label, fixedShortcut.Category));
        }
        var rows = HotkeyListSort.Sort(data, _hotkeySort)
            .Select(row => HotkeyCatalog.Find(row.ActionId) is { } definition
                ? CreateHotkeyRow(definition, row.Shortcut)
                : CreateFixedHotkeyRow(row))
            .ToList();
        HotkeyList.ItemsSource = rows;
        HotkeyList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        UpdateHotkeySortHeaders();
        UpdateHotkeyButtonState();
    }

    // ---- sortable column headers --------------------------------------------------------------------------

    private void HotkeySortShortcutClicked(object? sender, RoutedEventArgs e) => SortHotkeysBy(HotkeySortColumn.Shortcut);
    private void HotkeySortActionClicked(object? sender, RoutedEventArgs e) => SortHotkeysBy(HotkeySortColumn.Action);
    private void HotkeySortCategoryClicked(object? sender, RoutedEventArgs e) => SortHotkeysBy(HotkeySortColumn.Category);

    /// <summary>Clicking a header sorts by it ascending; clicking the same header again reverses the order.</summary>
    internal void SortHotkeysBy(HotkeySortColumn column)
    {
        _hotkeySort = _hotkeySort.Toggle(column);
        BuildHotkeyRows(HotkeyFilterBox?.Text);
    }

    private void UpdateHotkeySortHeaders()
    {
        void Set(Button? button, string title, HotkeySortColumn column)
        {
            if (button is null) return;
            var arrow = _hotkeySort.Indicator(column);
            button.Content = arrow.Length == 0 ? title : title + "  " + arrow;
        }
        Set(HotkeySortShortcutButton, "Shortcut", HotkeySortColumn.Shortcut);
        Set(HotkeySortActionButton, "Action", HotkeySortColumn.Action);
        Set(HotkeySortCategoryButton, "Category", HotkeySortColumn.Category);
    }

    // ---- "Search hotkey": press a key, see its action -------------------------------------------------------

    private void SearchHotkeyClicked(object? sender, RoutedEventArgs e)
    {
        if (_hotkeyLookupCapturing) EndHotkeyLookupCapture("Hotkey search cancelled.");
        else BeginHotkeyLookupCapture();
    }

    internal bool IsHotkeyLookupCapturing => _hotkeyLookupCapturing;

    internal void BeginHotkeyLookupCapture()
    {
        if (_hotkeyCaptureActionId is not null) EndHotkeyCapture("Shortcut capture cancelled.");
        _hotkeyLookupCapturing = true;
        SearchBox.IsEnabled = false;
        HotkeyFilterBox.IsEnabled = false;
        SearchHotkeyButton.Content = "Press a key...";
        HotkeyCaptureText.Text = "Press the key or key combination you want to look up. Press Esc to cancel. Nothing you press runs a command.";
        // Take focus off the button so Space / Enter are captured like any other key instead of clicking it again.
        Focus();
    }

    private void EndHotkeyLookupCapture(string message)
    {
        _hotkeyLookupCapturing = false;
        SearchBox.IsEnabled = true;
        HotkeyFilterBox.IsEnabled = true;
        SearchHotkeyButton.Content = "Search hotkey";
        HotkeyCaptureText.Text = message;
    }

    /// <summary>A key or combination was pressed during Search hotkey: show its action(s) or say it is unassigned.</summary>
    internal void FinishHotkeyLookup(string shortcut)
    {
        EndHotkeyLookupCapture(HotkeyLookup.Describe(_workingHotkeys, shortcut));
        _hotkeyLookupShortcut = shortcut;
        UpdateHotkeyFilterChrome();
        BuildHotkeyRows(HotkeyFilterBox?.Text);
    }

    /// <summary>"Show all hotkeys": leaves a Search hotkey result and clears the text filter, so every shortcut is listed.</summary>
    private void ClearHotkeyLookupClicked(object? sender, RoutedEventArgs e) => ShowAllHotkeys();

    /// <summary>The (x) inside the filter box: same as Show all hotkeys.</summary>
    private void ClearHotkeyFilterClicked(object? sender, RoutedEventArgs e)
    {
        ShowAllHotkeys();
        HotkeyFilterBox?.Focus();
    }

    internal void ShowAllHotkeys()
    {
        _hotkeyFilterTimer?.Stop();
        _hotkeyLookupShortcut = null;
        if (HotkeyFilterBox is not null && !string.IsNullOrEmpty(HotkeyFilterBox.Text))
        {
            var loading = _loadingControls;
            _loadingControls = true;           // no debounced rebuild: the list is rebuilt once below
            try { HotkeyFilterBox.Text = string.Empty; }
            finally { _loadingControls = loading; }
        }
        HotkeyCaptureText.Text = string.Empty;
        UpdateHotkeyFilterChrome();
        BuildHotkeyRows();
    }

    /// <summary>True while the shortcut list shows only part of the hotkeys (text filter or a Search hotkey result).</summary>
    internal bool IsHotkeyListFiltered => !string.IsNullOrWhiteSpace(HotkeyFilterBox?.Text) || _hotkeyLookupShortcut is not null;

    /// <summary>The (x) and "Show all hotkeys" are shown exactly while the list is filtered.</summary>
    private void UpdateHotkeyFilterChrome()
    {
        var filtered = IsHotkeyListFiltered;
        if (ClearHotkeyLookupButton is not null) ClearHotkeyLookupButton.IsVisible = filtered;
        if (ClearHotkeyFilterButton is not null) ClearHotkeyFilterButton.IsVisible = !string.IsNullOrEmpty(HotkeyFilterBox?.Text) || _hotkeyLookupShortcut is not null;
    }

    /// <summary>Leaves the Search hotkey result view and goes back to the normal list.</summary>
    private void ClearHotkeyLookup(bool rebuild)
    {
        _hotkeyLookupShortcut = null;
        UpdateHotkeyFilterChrome();
        if (rebuild) BuildHotkeyRows(HotkeyFilterBox?.Text);
    }

    private void UpdateExternalProgramHotkeyLabels()
    {
        if (External1Label is null || External2Label is null || External3Label is null) return;
        External1Label.Text = $"External Program 1 ({ExternalProgramShortcutText("external.1")})";
        External2Label.Text = $"External Program 2 ({ExternalProgramShortcutText("external.2")})";
        External3Label.Text = $"External Program 3 ({ExternalProgramShortcutText("external.3")})";
    }

    private string ExternalProgramShortcutText(string actionId)
    {
        var shortcuts = _workingHotkeys.GetValueOrDefault(actionId, new List<string>());
        return shortcuts.Count == 0 ? "Unassigned" : string.Join(" / ", shortcuts);
    }

    private void LoadHotkeyPresetClicked(object? sender, RoutedEventArgs e)
    {
        var preset = SelectedText(HotkeyPresetCombo, "Glide default");
        _workingHotkeys = HotkeyPresetLibrary.Resolve(_workingCustomPresets, preset);
        ClearHotkeyLookup(rebuild: false);
        HotkeyCaptureText.Text = HotkeyPresetLibrary.IsBuiltIn(preset) ? $"Loaded {preset} hotkey preset." : $"Loaded your custom hotkey preset \u201c{preset}\u201d.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    // ---- custom presets: save / rename / delete next to the built-ins -----------------------------------------------

    /// <summary>Rebuilds the preset list: the built-ins first (fixed XAML items), then the user's own presets.</summary>
    private void RebuildHotkeyPresetCombo(string? select = null)
    {
        if (HotkeyPresetCombo is null) return;
        var wasLoading = _loadingControls;
        _loadingControls = true;
        try
        {
            var current = select ?? SelectedText(HotkeyPresetCombo, "Glide default");
            var items = HotkeyPresetCombo.Items.OfType<ComboBoxItem>().ToList();
            foreach (var extra in items.Where(i => !HotkeyPresetLibrary.IsBuiltIn(i.Content?.ToString())).ToList())
                HotkeyPresetCombo.Items.Remove(extra);
            foreach (var preset in _workingCustomPresets)
                HotkeyPresetCombo.Items.Add(new ComboBoxItem { Content = preset.Name });
            var index = HotkeyPresetCombo.Items.OfType<ComboBoxItem>().ToList()
                .FindIndex(i => string.Equals(i.Content?.ToString(), current, StringComparison.OrdinalIgnoreCase));
            HotkeyPresetCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { _loadingControls = wasLoading; }
        UpdateHotkeyPresetButtons();
    }

    private void UpdateHotkeyPresetButtons()
    {
        if (RenameHotkeyPresetButton is null || DeleteHotkeyPresetButton is null) return;
        var custom = HotkeyPresetLibrary.Find(_workingCustomPresets, SelectedText(HotkeyPresetCombo)) is not null;
        RenameHotkeyPresetButton.IsEnabled = custom;
        DeleteHotkeyPresetButton.IsEnabled = custom;
    }

    private async void SaveHotkeyPresetClicked(object? sender, RoutedEventArgs e) => await SaveHotkeyPresetAsync();
    private async void RenameHotkeyPresetClicked(object? sender, RoutedEventArgs e) => await RenameHotkeyPresetAsync();
    private async void DeleteHotkeyPresetClicked(object? sender, RoutedEventArgs e) => await DeleteHotkeyPresetAsync();

    internal async Task SaveHotkeyPresetAsync()
    {
        var selected = SelectedText(HotkeyPresetCombo);
        var suggestion = HotkeyPresetLibrary.Find(_workingCustomPresets, selected)?.Name ?? SuggestPresetName();
        var requested = await PromptPresetNameAsync("Save hotkey preset", "Name this keyboard shortcut preset", suggestion, "Save");
        if (requested is null) return;
        var name = HotkeyPresetLibrary.NormalizeName(requested);
        if (HotkeyPresetLibrary.Find(_workingCustomPresets, name) is { } existing &&
            !await ConfirmAsync("Replace hotkey preset", $"A custom preset named \u201c{existing.Name}\u201d already exists. Replace it with your current shortcuts?", "Replace"))
            return;
        switch (HotkeyPresetLibrary.Save(_workingCustomPresets, name, _workingHotkeys, out var saved))
        {
            case HotkeyPresetResult.Ok:
            case HotkeyPresetResult.Overwritten:
                RebuildHotkeyPresetCombo(saved);
                HotkeyCaptureText.Text = $"Saved your shortcuts as the preset \u201c{saved}\u201d. Apply Settings to keep it.";
                PreviewHotkeyChange();
                break;
            case HotkeyPresetResult.BuiltInName:
                HotkeyCaptureText.Text = $"\u201c{saved}\u201d is a built-in preset name. Pick a different name.";
                break;
            case HotkeyPresetResult.LimitReached:
                HotkeyCaptureText.Text = $"You can keep up to {HotkeyPresetLibrary.MaxCustomPresets} custom presets. Delete one first.";
                break;
            default:
                HotkeyCaptureText.Text = "Enter a name for the preset.";
                break;
        }
    }

    internal async Task RenameHotkeyPresetAsync()
    {
        var old = HotkeyPresetLibrary.Find(_workingCustomPresets, SelectedText(HotkeyPresetCombo));
        if (old is null) { HotkeyCaptureText.Text = "Built-in presets cannot be renamed. Select one of your own presets."; return; }
        var requested = await PromptPresetNameAsync("Rename hotkey preset", $"New name for \u201c{old.Name}\u201d", old.Name, "Rename");
        if (requested is null) return;
        var oldName = old.Name;
        switch (HotkeyPresetLibrary.Rename(_workingCustomPresets, oldName, requested, out var renamed))
        {
            case HotkeyPresetResult.Ok:
                RebuildHotkeyPresetCombo(renamed);
                HotkeyCaptureText.Text = $"Renamed \u201c{oldName}\u201d to \u201c{renamed}\u201d.";
                PreviewHotkeyChange();
                break;
            case HotkeyPresetResult.NameInUse:
                HotkeyCaptureText.Text = $"A preset named \u201c{renamed}\u201d already exists.";
                break;
            case HotkeyPresetResult.BuiltInName:
                HotkeyCaptureText.Text = $"\u201c{renamed}\u201d is a built-in preset name. Pick a different name.";
                break;
            default:
                HotkeyCaptureText.Text = "Enter a name for the preset.";
                break;
        }
    }

    internal async Task DeleteHotkeyPresetAsync()
    {
        var preset = HotkeyPresetLibrary.Find(_workingCustomPresets, SelectedText(HotkeyPresetCombo));
        if (preset is null) { HotkeyCaptureText.Text = "Built-in presets cannot be deleted. Select one of your own presets."; return; }
        if (!await ConfirmAsync("Delete hotkey preset", $"Delete your preset \u201c{preset.Name}\u201d? Your current shortcuts are not changed.", "Delete")) return;
        var name = preset.Name;
        if (HotkeyPresetLibrary.Delete(_workingCustomPresets, name) != HotkeyPresetResult.Ok) return;
        RebuildHotkeyPresetCombo("Glide default");
        HotkeyCaptureText.Text = $"Deleted the preset \u201c{name}\u201d.";
        PreviewHotkeyChange();
    }

    private string SuggestPresetName()
    {
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"My shortcuts {i}";
            if (HotkeyPresetLibrary.Find(_workingCustomPresets, candidate) is null) return candidate;
        }
        return "My shortcuts";
    }

    /// <summary>Test seam: replaces the name dialog (title, suggested name) with a chosen name, or null for cancel.</summary>
    internal Func<string, string, Task<string?>>? PresetNamePromptOverride { get; set; }

    /// <summary>Test seam: replaces Replace / Cancel style dialogs (title, text) with true when confirmed.</summary>
    internal Func<string, string, Task<bool>>? ConfirmOverride { get; set; }

    private async Task<string?> PromptPresetNameAsync(string title, string heading, string initial, string okText)
    {
        if (PresetNamePromptOverride is { } fake) return await fake(title, initial);
        var box = new TextBox { Watermark = "Preset name", MinWidth = 300, Text = initial, MaxLength = HotkeyPresetLibrary.MaxNameLength };
        var ok = new Button { Content = okText, MinWidth = 90 };
        ok.Classes.Add("primaryAction");
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Classes.Add("secondaryAction");
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeight.SemiBold });
        root.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel); actions.Children.Add(ok); root.Children.Add(actions);
        var dialog = new Window
        {
            Title = title, CanResize = false, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 390, MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        PopupPlacementStore.Track(dialog, "hotkey-preset-name");
        ok.Click += (_, _) => dialog.Close(box.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close((string?)null);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { dialog.Close(box.Text?.Trim()); e.Handled = true; } };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    /// <summary>Small Replace / Cancel style confirmation. The primary button text is the action (Replace, Delete...).</summary>
    private async Task<bool> ConfirmAsync(string title, string text, string okText)
    {
        if (ConfirmOverride is { } fake) return await fake(title, text);
        var ok = new Button { Content = okText, MinWidth = 96 };
        ok.Classes.Add("primaryAction");
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        cancel.Classes.Add("secondaryAction");
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel); actions.Children.Add(ok); root.Children.Add(actions);
        var dialog = new Window
        {
            Title = title, CanResize = false, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 380, MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        PopupPlacementStore.Track(dialog, "hotkey-confirm");
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(false); e.Handled = true; } };
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(this);
    }

    private ListBoxItem CreateHotkeyRow(HotkeyActionDefinition action, string shortcut)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(HotkeyColumns) };
        var shortcutText = new TextBlock { Text = string.IsNullOrWhiteSpace(shortcut) ? "(unassigned)" : shortcut, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var actionText = new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var categoryText = new TextBlock { Text = action.Category, VerticalAlignment = VerticalAlignment.Center, Foreground = SearchMutedBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(actionText, 1); Grid.SetColumn(categoryText, 2);
        grid.Children.Add(shortcutText); grid.Children.Add(actionText); grid.Children.Add(categoryText);
        var item = new ListBoxItem { Content = grid, Tag = new HotkeyRowTag(action.Id, shortcut), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        item.Classes.Add("hotkeyRow");
        ToolTip.SetTip(item, $"{action.Label} — double-click to change this shortcut");
        return item;
    }

    /// <summary>A read-only row for a fixed shortcut: shown and searchable, never editable.</summary>
    private ListBoxItem CreateFixedHotkeyRow(HotkeyListRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(HotkeyColumns), Opacity = 0.78 };
        var shortcutText = new TextBlock { Text = row.Shortcut, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var actionText = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var categoryText = new TextBlock { Text = row.Category, VerticalAlignment = VerticalAlignment.Center, Foreground = SearchMutedBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(actionText, 1); Grid.SetColumn(categoryText, 2);
        grid.Children.Add(shortcutText); grid.Children.Add(actionText); grid.Children.Add(categoryText);
        var item = new ListBoxItem { Content = grid, Tag = new HotkeyRowTag(row.ActionId, row.Shortcut) { ReadOnly = true }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        item.Classes.Add("hotkeyRow");
        item.Classes.Add("fixedHotkeyRow");
        var description = HotkeyCatalog.FixedShortcuts.FirstOrDefault(f => f.Id == row.ActionId)?.Description;
        ToolTip.SetTip(item, $"{row.Label}: fixed shortcut, not editable. {description}");
        return item;
    }

    private void HotkeySelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateHotkeyButtonState();

    private void UpdateHotkeyButtonState()
    {
        var row = (HotkeyList?.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        var editable = row is { ReadOnly: false };
        ChangeShortcutButton.IsEnabled = editable;
        AddShortcutButton.IsEnabled = editable;
        RemoveShortcutButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(row!.Shortcut);
    }

    private void HotkeyListDoubleTapped(object? sender, TappedEventArgs e) => StartHotkeyCapture(add: false);
    private void ChangeShortcutClicked(object? sender, RoutedEventArgs e) => StartHotkeyCapture(add: false);
    private void AddShortcutClicked(object? sender, RoutedEventArgs e) => StartHotkeyCapture(add: true);

    private void StartHotkeyCapture(bool add)
    {
        var row = (HotkeyList.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        if (row is null || row.ReadOnly) return;
        StartHotkeyCaptureFor(row.ActionId, row.Shortcut, add, fromSearch: false);
    }

    private void StartHotkeyCaptureFor(string actionId, string? oldShortcut, bool add, bool fromSearch, Border? searchCard = null)
    {
        ClearHotkeyCaptureFlash();
        _hotkeyCaptureActionId = actionId;
        _hotkeyCaptureOldShortcut = oldShortcut;
        _hotkeyCaptureAdd = add;
        _hotkeyCaptureFromSearch = fromSearch;
        _hotkeyCaptureSearchCard = searchCard;
        var action = HotkeyCatalog.Find(actionId);
        var message = $"Waiting for a new shortcut for {action?.Label ?? actionId}. Press the keyboard shortcut now, press Esc to cancel, or click anywhere else in Settings to leave edit mode.";
        HotkeyCaptureText.Text = message;
        if (fromSearch) PageSubtitle.Text = message;
        SearchBox.IsEnabled = false;

        if (fromSearch && _hotkeyCaptureSearchCard is not null)
        {
            _hotkeyFlashOn = true;
            SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, true);
            _hotkeyCaptureFlashTimer.Start();
        }
        else if (!fromSearch && HotkeyList.ItemsSource is IEnumerable<ListBoxItem> rows)
        {
            _hotkeyCaptureRow = rows.FirstOrDefault(row =>
            {
                if (row.Tag is not HotkeyRowTag tag || !string.Equals(tag.ActionId, actionId, StringComparison.OrdinalIgnoreCase)) return false;
                return add || string.Equals(tag.Shortcut, oldShortcut ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }) ?? rows.FirstOrDefault(row => row.Tag is HotkeyRowTag tag && string.Equals(tag.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
            if (_hotkeyCaptureRow is not null)
            {
                _hotkeyFlashOn = true;
                SetHotkeyCaptureFlash(_hotkeyCaptureRow, true);
                _hotkeyCaptureFlashTimer.Start();
            }
        }
        Focus();
    }

    private void RemoveShortcutClicked(object? sender, RoutedEventArgs e)
    {
        var row = (HotkeyList.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        if (row is null || row.ReadOnly || string.IsNullOrWhiteSpace(row.Shortcut)) return;
        if (_workingHotkeys.TryGetValue(row.ActionId, out var values))
            values.RemoveAll(x => string.Equals(x, row.Shortcut, StringComparison.OrdinalIgnoreCase));
        ClearHotkeyLookup(rebuild: false);
        HotkeyCaptureText.Text = $"Removed {row.Shortcut}.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private void ResetHotkeysClicked(object? sender, RoutedEventArgs e)
    {
        _workingHotkeys = HotkeyCatalog.CreateDefaultMap();
        ClearHotkeyLookup(rebuild: false);
        HotkeyCaptureText.Text = "Restored Glide default hotkeys.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private void SettingsKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+F focuses Settings search (not while a shortcut is being captured, so Ctrl+F stays assignable).
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control
            && _hotkeyCaptureActionId is null && !_hotkeyLookupCapturing)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_hotkeyCaptureActionId is not null)
            {
                EndHotkeyCapture("Shortcut capture cancelled.");
                return;
            }
            if (_hotkeyLookupCapturing)
            {
                EndHotkeyLookupCapture("Hotkey search cancelled.");
                return;
            }

            // Escape is the keyboard equivalent of Cancel: it uses the same discard-confirmation
            // and preview-rollback path instead of bypassing unsaved-settings protection.
            CancelClicked(this, new RoutedEventArgs());
            return;
        }

        // Search hotkey: the pressed key is only looked up. It is always swallowed here, so it can never
        // activate a button or run a command while the user is just asking what it does.
        if (_hotkeyLookupCapturing)
        {
            e.Handled = true;
            var looked = KeyboardShortcutText.From(e);
            if (string.IsNullOrWhiteSpace(looked) || IsModifierOnly(e.Key)) return;
            FinishHotkeyLookup(looked);
            return;
        }

        if (_hotkeyCaptureActionId is null) return;
        e.Handled = true;
        var shortcut = KeyboardShortcutText.From(e);
        if (string.IsNullOrWhiteSpace(shortcut) || IsModifierOnly(e.Key)) return;
        AssignCapturedShortcut(shortcut);
    }

    private async void AssignCapturedShortcut(string shortcut)
    {
        try { await AssignCapturedShortcutAsync(shortcut); }
        catch (Exception ex) { HotkeyCaptureText.Text = $"Could not assign the shortcut: {ex.Message}"; }
    }

    /// <summary>
    /// Applies a captured shortcut. A shortcut can have one owner: when another action already uses it, the user is
    /// asked to Replace (the old owner loses it) or Cancel (nothing changes). Re-assigning a shortcut the action
    /// already has changes nothing and asks nothing.
    /// </summary>
    internal async Task AssignCapturedShortcutAsync(string shortcut)
    {
        if (_hotkeyCaptureActionId is not { } actionId) return;
        var oldShortcut = _hotkeyCaptureOldShortcut;
        var add = _hotkeyCaptureAdd;
        var fromSearch = _hotkeyCaptureFromSearch;
        var action = HotkeyCatalog.Find(actionId);
        var label = action?.Label ?? actionId;

        var conflict = HotkeyConflicts.Check(_workingHotkeys, shortcut, actionId);
        var replace = false;
        if (conflict.NeedsConfirmation)
        {
            // Leave capture mode while the question is open, so no further key is captured behind the dialog.
            EndHotkeyCapture($"{shortcut} is already assigned to {conflict.OwnerNames}. Choose Replace or Cancel.");
            replace = await ConfirmAsync("Shortcut already assigned", conflict.PromptText(), "Replace");
            if (!replace)
            {
                var kept = $"Cancelled. {shortcut} stays assigned to {conflict.OwnerNames}; {label} was not changed.";
                HotkeyCaptureText.Text = kept;
                if (fromSearch) PageSubtitle.Text = kept;
                return;
            }
        }

        var outcome = HotkeyConflicts.Assign(_workingHotkeys, shortcut, actionId, oldShortcut, add, replaceOthers: replace);
        string message;
        if (outcome == HotkeyAssignOutcome.AlreadyAssigned) message = $"{shortcut} is already assigned to {label}. Nothing changed.";
        else if (replace) message = $"Assigned {shortcut} to {label}; removed it from {conflict.OwnerNames}.";
        else message = $"Assigned {shortcut} to {label}.";

        if (conflict.NeedsConfirmation)
        {
            HotkeyCaptureText.Text = message;
            if (fromSearch) PageSubtitle.Text = message;
            foreach (var owner in conflict.Owners) RefreshHotkeySearchDisplay(owner.ActionId);
            RefreshHotkeySearchDisplay(actionId);
        }
        else EndHotkeyCapture(message);
        // the list keeps the user's filter (and a Search hotkey result) after an assignment.
        BuildHotkeyRows(HotkeyFilterBox?.Text);
        PreviewHotkeyChange();
    }

    private void EndHotkeyCapture(string message)
    {
        var wasSearch = _hotkeyCaptureFromSearch;
        var actionId = _hotkeyCaptureActionId;
        ClearHotkeyCaptureFlash();
        _hotkeyCaptureActionId = null;
        _hotkeyCaptureOldShortcut = null;
        _hotkeyCaptureAdd = false;
        _hotkeyCaptureFromSearch = false;
        _hotkeyCaptureSearchCard = null;
        SearchBox.IsEnabled = true;
        HotkeyCaptureText.Text = message;
        if (wasSearch)
        {
            if (!string.IsNullOrWhiteSpace(actionId)) RefreshHotkeySearchDisplay(actionId);
            PageSubtitle.Text = message;
            // Deliberately do not rebuild SearchPanel here: rebuilding destroys focus/flash state and
            // resets ContentScroll to the top. The visible row is updated in place instead.
        }
    }

    private void ToggleHotkeyCaptureFlash()
    {
        if (_hotkeyCaptureActionId is null || (_hotkeyCaptureRow is null && _hotkeyCaptureSearchCard is null))
        {
            ClearHotkeyCaptureFlash();
            return;
        }
        _hotkeyFlashOn = !_hotkeyFlashOn;
        if (_hotkeyCaptureRow is not null) SetHotkeyCaptureFlash(_hotkeyCaptureRow, _hotkeyFlashOn);
        if (_hotkeyCaptureSearchCard is not null) SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, _hotkeyFlashOn);
    }

    private static void SetHotkeyCaptureFlash(Control row, bool visible)
    {
        if (visible)
        {
            if (!row.Classes.Contains("capturePending")) row.Classes.Add("capturePending");
        }
        else row.Classes.Remove("capturePending");
    }

    private void ClearHotkeyCaptureFlash()
    {
        _hotkeyCaptureFlashTimer.Stop();
        if (_hotkeyCaptureRow is not null) SetHotkeyCaptureFlash(_hotkeyCaptureRow, false);
        if (_hotkeyCaptureSearchCard is not null) SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, false);
        _hotkeyCaptureRow = null;
        _hotkeyCaptureSearchCard = null;
        _hotkeyFlashOn = false;
    }

    private void PreviewHotkeyChange()
    {
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private static bool IsModifierOnly(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private sealed record HotkeyRowTag(string ActionId, string Shortcut)
    {
        /// <summary>A fixed shortcut from the registry: listed, never editable.</summary>
        public bool ReadOnly { get; init; }
    }
}
