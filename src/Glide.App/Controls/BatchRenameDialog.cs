using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Glide.App.Explorer;
using Glide.App.Renaming;
using Glide.Core;
using Glide.Core.Renaming;

namespace Glide.App.Controls;

/// <summary>What the batch rename dialog changed: the (old path, new path) pairs, and whether it was "Undo last rename".</summary>
internal sealed record BatchRenameDialogResult(IReadOnlyList<(string From, string To)> Renamed, bool IsUndo = false);

/// <summary>
/// Batch rename (GitHub issue #4, rebuilt in 5.2.0 from the approved mockup <c>docs/feedback/mockups/batch-rename-v3</c>).
/// A "New name" box that mixes typed text with blue pieces (Old name, Number, Date taken, Date modified, Folder name, Size) added
/// by the chips under it; three small boxes (Replace text, Numbers, Capitals); "More options" (date format, remove characters,
/// extension, sort order, match case, regular expression for Replace); "Saved:" presets; and a live Now / After table.
/// Rows are green ticks for a rename, "⚠ name taken" in red for a conflict and "skipped" when a date piece has no date.
/// Dates and sizes are read once per file on a background thread (<see cref="RenameFactsCache"/>); a long list is also planned
/// in the background after a short pause, so typing stays smooth. Rename runs <see cref="BatchRenameExecutor"/> (two phases through
/// temporary names, all or nothing) for the items without a problem; "Undo last rename" turns the last batch around the same way.
/// The old single-field pattern language (<c>holiday*</c>, <c>foo/bar</c>) is still read when such text is put in the box, but nothing
/// in the dialog mentions it.
/// </summary>
internal sealed class BatchRenameDialog : Window
{
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static readonly IBrush BackBrush = Brush("#1B1D21");
    private static readonly IBrush CardBrush = Brush("#24272C");
    private static readonly IBrush LineBrush = Brush("#3A3F46");
    private static readonly IBrush TextBrush = Brush("#E8EAED");
    private static readonly IBrush DimBrush = Brush("#9AA0A6");
    private static readonly IBrush AccentBrush = Brush("#4EA1FF");
    private static readonly IBrush OkBrush = Brush("#5CCF8A");
    private static readonly IBrush BadBrush = Brush("#F06A6A");
    private static readonly IBrush InputBrush = Brush("#15171A");
    private static readonly IBrush RowLineBrush = Brush("#2C3036");
    private static readonly IBrush BadRowBrush = Brush("#2A1A1A");
    private static readonly IBrush FooterBrush = Brush("#1E2024");

    /// <summary>Lists up to this long are planned on the UI thread at once when nothing has to be read; longer ones wait a moment after typing and are planned in the background.</summary>
    internal const int SyncLimit = 100;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);

    private readonly IReadOnlyList<BatchRenameSource> _sources;
    private readonly Func<string, bool> _pathExists;
    private readonly IReadOnlyList<(string From, string To)>? _lastBatch;
    private readonly DispatcherTimer _debounce;
    private readonly List<TaskCompletionSource> _idleWaiters = [];
    private IReadOnlyList<RenamePreset> _presets;
    private string? _currentPreset;
    private CancellationTokenSource? _planCancel;
    private int _generation;
    private int _inFlight;
    private bool _loading;
    private bool _renaming;
    private string? _messageOverride;
    private RenameRecipe _pendingRecipe = new();

    public RenameTemplateEditor Editor { get; } = new();
    public List<Button> ChipButtons { get; } = [];

    public TextBox FindBox { get; } = new() { Name = "BatchRenameFind" };
    public TextBox ReplaceBox { get; } = new() { Name = "BatchRenameReplace" };
    public TextBox StartBox { get; } = new() { Name = "BatchRenameStart", Text = "1", Width = 52 };
    public ComboBox LookCombo { get; } = new() { Name = "BatchRenameLook" };
    public ComboBox CapitalsCombo { get; } = new() { Name = "BatchRenameCapitals" };

    public Button MoreToggle { get; } = new() { Name = "BatchRenameMore", Content = "▸ More options", Focusable = true };
    public Border MoreCard { get; } = new() { Name = "BatchRenameMoreCard", IsVisible = false };
    public ComboBox DateFormatCombo { get; } = new() { Name = "BatchRenameDateFormat" };
    public TextBox CustomDateBox { get; } = new() { Name = "BatchRenameCustomDate", Watermark = "yyyy-MM-dd", Width = 150, IsVisible = false };
    public TextBox RemoveFirstBox { get; } = new() { Name = "BatchRenameRemoveFirst", Text = "0", Width = 52 };
    public TextBox RemoveLastBox { get; } = new() { Name = "BatchRenameRemoveLast", Text = "0", Width = 52 };
    public CheckBox RemoveSpacesCheck { get; } = new() { Name = "BatchRenameRemoveSpaces", Content = "extra spaces" };
    public CheckBox RemoveDigitsCheck { get; } = new() { Name = "BatchRenameRemoveDigits", Content = "digits" };
    public ComboBox ExtensionCombo { get; } = new() { Name = "BatchRenameExtension" };
    public ComboBox SortCombo { get; } = new() { Name = "BatchRenameSort" };
    public CheckBox MatchCaseCheck { get; } = new() { Name = "BatchRenameMatchCase", Content = "Match case" };
    public CheckBox RegexCheck { get; } = new() { Name = "BatchRenameRegex", Content = "Regular expression (Replace text)" };

    public Button PresetsButton { get; } = new() { Name = "BatchRenamePresets" };
    public TextBox PresetNameBox { get; } = new() { Name = "BatchRenamePresetName", Watermark = "Name for this setup", MinWidth = 180 };
    public Button SavePresetButton { get; } = new() { Name = "BatchRenameSavePreset", Content = "Save" };
    private readonly StackPanel _presetList = new() { Spacing = 2 };

    public ItemsControl PreviewList { get; } = new() { Name = "BatchRenamePreview" };
    public TextBlock StatusText { get; } = new() { Name = "BatchRenameStatus", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    public Button UndoButton { get; } = new() { Name = "BatchRenameUndo", Content = "↶ Undo last rename" };
    public Button RenameButton { get; } = new() { Name = "BatchRenameAccept", Content = "Rename", MinWidth = 100, IsDefault = true, IsEnabled = false };
    public Button CancelButton { get; } = new() { Name = "BatchRenameCancel", Content = "Cancel", MinWidth = 80, IsCancel = true };

    public BatchRenamePlan CurrentPlan { get; private set; } = new([], null);

    /// <summary>What is read from the files (dates, sizes); tests swap its probes.</summary>
    internal RenameFactsCache Facts { get; } = new();

    /// <summary>The recipe the boxes say now.</summary>
    public RenameRecipe Recipe => ReadRecipe();

    /// <summary>The saved presets (newest first).</summary>
    public IReadOnlyList<RenamePreset> Presets => _presets;

    /// <summary>Raised with the new settings string whenever a preset is saved or deleted (the window stores it).</summary>
    public event Action<string>? PresetsChanged;

    /// <summary>Test seam: runs the rename. Defaults to the all-or-nothing file system executor.</summary>
    internal Func<IReadOnlyList<(string From, string To)>, BatchRenameResult> Executor { get; set; } = BatchRenameExecutor.Execute;

    public BatchRenameDialog(IReadOnlyList<BatchRenameSource> sources, string? presetsJson = null, Func<string, bool>? pathExists = null,
        IReadOnlyList<(string From, string To)>? lastBatch = null)
    {
        _sources = sources;
        _presets = RenamePresets.Parse(presetsJson);
        _pathExists = pathExists ?? (p => File.Exists(p) || Directory.Exists(p));
        _lastBatch = lastBatch is { Count: > 0 } ? lastBatch : null;
        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunInBackgroundAsync(_pendingRecipe, _generation); };
        Title =sources.Count == 1 ? "Rename" : sources.All(s => !s.IsDirectory && ImageNavigator.IsSupported(s.Path)) ? $"Rename {sources.Count} pictures" : $"Rename {sources.Count} items";
        Width = 980;
        Height = 760;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // After the default and minimum size are set: restores a remembered size, else centres and fits the work area.
        Glide.App.Services.WindowPlacementService.Track(this, "batch-rename");
        Background = BackBrush;
        Foreground = TextBrush;
        FontSize = 13;
        AddStyles();
        Editor.Template = RenameTemplate.Default;   // "Old name": nothing changes until the user adds something
        Content = BuildContent();
        Wire();
        UpdatePresetsButton();
        UpdateUndoButton();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Editor.FocusEnd(), DispatcherPriority.Input);
        Closed += (_, _) => { _debounce.Stop(); _planCancel?.Cancel(); };
        OnChanged();
    }

    // ------------------------------------------------------------------ layout

    private void AddStyles()
    {
        // The Fluent button draws its hover colour on the content presenter; these keep the mockup's colours there too.
        void Hover(string cls, string hover, string? pressed = null, string? disabledBack = null)
        {
            Styles.Add(PresenterStyle(cls, ":pointerover", hover));
            Styles.Add(PresenterStyle(cls, ":pressed", pressed ?? hover));
            if (disabledBack is not null) Styles.Add(PresenterStyle(cls, ":disabled", disabledBack));
        }
        Hover("rn-chip", "#2B4560");
        Hover("rn-btn", "#343A42", "#3B424B", "#23262B");
        Hover("rn-primary", "#6AB2FF", "#3E90E8", "#2A2E34");
        Hover("rn-flat", "#2A2E34");
    }

    private static Style PresenterStyle(string cls, string pseudo, string background) =>
        new(x => x.OfType<Button>().Class(cls).Class(pseudo).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brush(background)) }
        };

    private static TextBlock Dim(string text, double size = 12, string? name = null) =>
        new() { Text = text, Foreground = DimBrush, FontSize = size, Name = name, VerticalAlignment = VerticalAlignment.Center };

    private static void StyleInput(TemplatedControl box)
    {
        box.Background = InputBrush;
        box.BorderBrush = LineBrush;
        box.Foreground = TextBrush;
        box.CornerRadius = new CornerRadius(5);
        box.MinHeight = 30;
        if (box is TextBox t) { t.Padding = new Thickness(9, 5); t.VerticalContentAlignment = VerticalAlignment.Center; t.FontSize = 13; }
        if (box is ComboBox c) { c.Padding = new Thickness(9, 4); c.VerticalContentAlignment = VerticalAlignment.Center; c.FontSize = 13; }
    }

    private static void StyleButton(Button b, string cls, string back, string fore, string border)
    {
        b.Classes.Add(cls);
        b.Background = Brush(back);
        b.Foreground = Brush(fore);
        b.BorderBrush = Brush(border);
        b.BorderThickness = new Thickness(1);
        b.CornerRadius = new CornerRadius(6);
        b.Padding = new Thickness(14, 7);
        b.FontSize = 13;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private static Border Card(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 7, Children = { Dim(title), content } };
        return new Border { Background = CardBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Child = stack };
    }

    private Grid BuildContent()
    {
        foreach (var combo in new[] { LookCombo, CapitalsCombo, DateFormatCombo, ExtensionCombo, SortCombo }) StyleInput(combo);
        foreach (var box in new[] { FindBox, ReplaceBox, StartBox, CustomDateBox, RemoveFirstBox, RemoveLastBox, PresetNameBox }) StyleInput(box);
        FindBox.Watermark = "find";
        ReplaceBox.Watermark = "replace with";
        LookCombo.ItemsSource = new[] { "1", "01", "001", "0001" };
        LookCombo.SelectedIndex = 0;
        CapitalsCombo.ItemsSource = new[] { "Keep", "lower", "UPPER", "Title Case", "Sentence case" };
        CapitalsCombo.SelectedIndex = 0;
        CapitalsCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        DateFormatCombo.ItemsSource = RenameDateFormats.Presets.Select(RenameDateFormats.Example).Append("Custom…").ToArray();
        DateFormatCombo.SelectedIndex = 0;
        ExtensionCombo.ItemsSource = new[] { "Keep", "lower case", "UPPER CASE" };
        ExtensionCombo.SelectedIndex = 0;
        SortCombo.ItemsSource = new[] { "Current view order", "Name", "Date taken", "Date modified" };
        SortCombo.SelectedIndex = 0;
        foreach (var check in new[] { RemoveSpacesCheck, RemoveDigitsCheck, MatchCaseCheck, RegexCheck }) { check.FontSize = 13; check.Foreground = TextBrush; }
        ToolTip.SetTip(StartBox, "The first number");
        ToolTip.SetTip(RegexCheck, "Read the Replace text boxes as a .NET regular expression; the replacement can use $1, $2 ...");
        ToolTip.SetTip(MatchCaseCheck, "Off: IMG also finds img and Img.");
        ToolTip.SetTip(SortCombo, "The order the numbers are given in");
        ToolTip.SetTip(DateFormatCombo, "How Date taken and Date modified are written");

        // New name
        var label = Dim("New name — type text and click the blue pieces to add them");
        label.Margin = new Thickness(0, 0, 0, 6);
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var add = Dim("Add:");
        add.Margin = new Thickness(0, 0, 8, 6);
        chips.Children.Add(add);
        foreach (var info in RenameTemplate.Tokens)
        {
            var chip = new Button { Name = "BatchRenameChip_" + info.Key, Content = "＋ " + info.ChipLabel, Focusable = false, Margin = new Thickness(0, 0, 6, 6), Tag = info.Token };
            StyleButton(chip, "rn-chip", "#22303F", "#CFE5FF", "#3A3F46");
            chip.CornerRadius = new CornerRadius(14);
            chip.Padding = new Thickness(11, 4);
            chip.MinHeight = 0;
            var token = info.Token;
            chip.Click += (_, _) => Editor.Insert(token);
            ChipButtons.Add(chip);
            chips.Children.Add(chip);
        }
        var nameBlock = new StackPanel { Children = { label, Editor, chips } };

        // Three small boxes
        var replace = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 8 };
        var arrow = Dim("→", 14);
        Grid.SetColumn(arrow, 1); Grid.SetColumn(ReplaceBox, 2);
        replace.Children.Add(FindBox); replace.Children.Add(arrow); replace.Children.Add(ReplaceBox);
        // A grid (not a horizontal StackPanel) so the boxes shrink with the card instead of spilling past its edge.
        var numbers = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,1.4*"), ColumnSpacing = 8 };
        var startLabel = Dim("Start", 13); var lookLabel = Dim("Look", 13);
        startLabel.VerticalAlignment = lookLabel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        StartBox.MinWidth = 36; StartBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        LookCombo.MinWidth = 0; LookCombo.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        Grid.SetColumn(StartBox, 1); Grid.SetColumn(lookLabel, 2); Grid.SetColumn(LookCombo, 3);
        numbers.Children.Add(startLabel); numbers.Children.Add(StartBox); numbers.Children.Add(lookLabel); numbers.Children.Add(LookCombo);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1*,0.8*,0.7*"), ColumnSpacing = 14 };
        var c1 = Card("Replace text", replace);
        var c2 = Card("Numbers", numbers);
        var c3 = Card("Capitals", CapitalsCombo);
        Grid.SetColumn(c2, 1); Grid.SetColumn(c3, 2);
        row.Children.Add(c1); row.Children.Add(c2); row.Children.Add(c3);

        // More options toggle, saved presets
        StyleButton(MoreToggle, "rn-flat", "#00000000", "#4EA1FF", "#00000000");
        MoreToggle.Padding = new Thickness(0, 4);
        MoreToggle.FontSize = 12;
        MoreToggle.BorderThickness = new Thickness(0);
        StyleInput(PresetsButton);
        StyleButton(PresetsButton, "rn-btn", "#15171A", "#E8EAED", "#3A3F46");
        PresetsButton.Padding = new Thickness(12, 5);
        PresetsButton.Flyout = BuildPresetFlyout();
        var more = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        var note = Dim("— date format, remove characters, extension, sort order, match case, regular expressions");
        note.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(note, 1); Grid.SetColumn(PresetsButton, 2);
        more.Children.Add(MoreToggle); more.Children.Add(note); more.Children.Add(PresetsButton);

        MoreCard.Child = BuildMoreOptions();
        MoreCard.Background = CardBrush;
        MoreCard.BorderBrush = LineBrush;
        MoreCard.BorderThickness = new Thickness(1);
        MoreCard.CornerRadius = new CornerRadius(8);
        MoreCard.Padding = new Thickness(12, 10);

        // Table
        PreviewList.ItemTemplate = new FuncDataTemplate<BatchRenameItem>((item, _) => BuildPreviewRow(item), supportsRecycling: false);
        PreviewList.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        PreviewList.Template = new FuncControlTemplate<ItemsControl>((ic, scope) => new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new ItemsPresenter { [!ItemsPresenter.ItemsPanelProperty] = ic[!ItemsControl.ItemsPanelProperty] }
        });
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,6*,110"), Margin = new Thickness(0, 0, 0, 0) };
        var now = HeaderText("Now"); var after = HeaderText("After");
        Grid.SetColumn(after, 1);
        header.Children.Add(now); header.Children.Add(after);
        var headerBorder = new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(PreviewList, 1);
        table.Children.Add(headerBorder); table.Children.Add(PreviewList);

        // The body scrolls: the preview list always keeps at least PreviewMinHeight, and when the window is too short for that
        // (e.g. "More options" open) the whole body scrolls instead of squeezing the list to nothing.
        var top = new StackPanel { Spacing = 14, Children = { nameBlock, row, more, MoreCard } };
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 14, Margin = new Thickness(22, 18) };
        Grid.SetRow(table, 1);
        main.Children.Add(top); main.Children.Add(table);
        var bodyScroll = new ScrollViewer { Name = "BatchRenameBodyScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = main };
        void FitBody()
        {
            var viewport = bodyScroll.Bounds.Height;
            if (viewport <= 0) return;
            var needed = top.DesiredSize.Height + 14 + PreviewMinHeight + 36;
            var h = Math.Max(viewport, needed);
            if (Math.Abs(main.Height - h) > 0.5 || double.IsNaN(main.Height)) main.Height = h;
        }
        // Recomputed after layout (top's DesiredSize is current then); only when the viewport or the top block's height changes.
        bodyScroll.SizeChanged += (_, _) => FitBody();
        top.SizeChanged += (_, e) => { if (e.HeightChanged) FitBody(); };

        // Footer
        StyleButton(UndoButton, "rn-btn", "#2A2E34", "#E8EAED", "#3A3F46");
        StyleButton(CancelButton, "rn-btn", "#2A2E34", "#E8EAED", "#3A3F46");
        StyleButton(RenameButton, "rn-primary", "#4EA1FF", "#06121F", "#4EA1FF");
        RenameButton.FontWeight = FontWeight.SemiBold;
        StatusText.Foreground = DimBrush;
        StatusText.FontSize = 12;
        StatusText.VerticalAlignment = VerticalAlignment.Center;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        StatusText.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(StatusText, 1); Grid.SetColumn(CancelButton, 2); Grid.SetColumn(RenameButton, 3);
        footer.Children.Add(UndoButton); footer.Children.Add(StatusText); footer.Children.Add(CancelButton); footer.Children.Add(RenameButton);
        var footerBorder = new Border { Background = FooterBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 12), Child = footer };

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(bodyScroll); root.Children.Add(footerBorder);
        return root;
    }

    private const double PreviewMinHeight = 220;

    private static TextBlock HeaderText(string text) =>
        new() { Text = text, Foreground = DimBrush, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 7) };

    private StackPanel BuildMoreOptions()
    {
        var removeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Dim("Remove", 13), Dim("first", 13), RemoveFirstBox, Dim("last", 13), RemoveLastBox, Dim("characters of the old name,", 13), RemoveDigitsCheck, RemoveSpacesCheck }
        };
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Dim("Date format", 13), DateFormatCombo, CustomDateBox } };
        var extRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Dim("Extension", 13), ExtensionCombo } };
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Dim("Sort order before numbering", 13), SortCombo } };
        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var part in new Control[] { dateRow, extRow, sortRow }) { part.Margin = new Thickness(0, 0, 22, 8); top.Children.Add(part); }
        var matchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { MatchCaseCheck, RegexCheck } };
        return new StackPanel { Spacing = 8, Children = { top, removeRow, matchRow } };
    }

    private Flyout BuildPresetFlyout()
    {
        StyleInput(PresetNameBox);
        StyleButton(SavePresetButton, "rn-btn", "#2A2E34", "#E8EAED", "#3A3F46");
        SavePresetButton.Padding = new Thickness(12, 5);
        var saveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(SavePresetButton, 1);
        saveRow.Children.Add(PresetNameBox); saveRow.Children.Add(SavePresetButton);
        var heading = Dim("Saved setups — click one to load it");
        var panel = new StackPanel { MinWidth = 300, Spacing = 4, Children = { heading, _presetList, new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 4) }, Dim("Save the boxes as they are now"), saveRow } };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) =>
        {
            PresetNameBox.Text = _currentPreset ?? "";
            FillPresetList(flyout);
        };
        SavePresetButton.Click += (_, _) =>
        {
            if (SavePreset(PresetNameBox.Text)) flyout.Hide();
        };
        PresetNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; if (SavePreset(PresetNameBox.Text)) flyout.Hide(); }
        };
        return flyout;
    }

    private void FillPresetList(Flyout flyout)
    {
        _presetList.Children.Clear();
        if (_presets.Count == 0)
        {
            _presetList.Children.Add(Dim("Nothing saved yet."));
            return;
        }
        foreach (var preset in _presets)
        {
            var name = preset.Name;
            var load = new Button { Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            StyleButton(load, "rn-flat", "#00000000", "#E8EAED", "#00000000");
            load.HorizontalContentAlignment = HorizontalAlignment.Left;
            load.BorderThickness = new Thickness(0);
            load.Padding = new Thickness(8, 5);
            load.Click += (_, _) => { LoadPreset(name); flyout.Hide(); };
            var delete = new Button { Content = "✕" };
            StyleButton(delete, "rn-flat", "#00000000", "#9AA0A6", "#00000000");
            delete.BorderThickness = new Thickness(0);
            delete.Padding = new Thickness(8, 5);
            ToolTip.SetTip(delete, "Delete this saved setup");
            delete.Click += (_, _) => { DeletePreset(name); FillPresetList(flyout); };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(delete, 1);
            line.Children.Add(load); line.Children.Add(delete);
            _presetList.Children.Add(line);
        }
    }

    // ------------------------------------------------------------------ preview rows

    private static Control BuildPreviewRow(BatchRenameItem? item)
    {
        if (item is null) return new TextBlock();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,6*,110") };
        var old = new TextBlock { Name = "OldNameText", Text = item.OldName, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 7), VerticalAlignment = VerticalAlignment.Center };

        var after = new TextBlock { Name = "NewNameText", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 7), VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush };
        var status = new TextBlock { Name = "RowStatus", FontSize = 12, Margin = new Thickness(8, 7), VerticalAlignment = VerticalAlignment.Center };
        var bad = item.HasProblem;
        if (item.Skipped)
        {
            after.Text = $"{item.SkipReason} — left as is";
            after.Foreground = DimBrush;
            status.Text = "skipped";
            status.Foreground = DimBrush;
        }
        else if (bad)
        {
            after.Text = item.NewName;
            status.Text = item.Problem switch
            {
                BatchRenameProblem.InvalidName => "⚠ not allowed",
                BatchRenameProblem.ExpressionFailed => "⚠ error",
                _ => "⚠ name taken"
            };
            status.Foreground = BadBrush;
            ToolTip.SetTip(after, item.Message);
            ToolTip.SetTip(status, item.Message);
        }
        else if (!item.Changed)
        {
            after.Text = item.NewName;
            after.Foreground = DimBrush;
            status.Text = "unchanged";
            status.Foreground = DimBrush;
        }
        else
        {
            if (item.Segments is { Count: > 0 } segments)
            {
                foreach (var segment in segments)
                {
                    var run = new Run(segment.Text);
                    if (segment.IsToken) { run.Background = RenameTemplateEditor.TokenBack; run.Foreground = Brushes.White; }
                    after.Inlines!.Add(run);
                }
            }
            else after.Text = item.NewName;
            status.Text = "✓";
            status.Foreground = OkBrush;
        }
        Grid.SetColumn(after, 1); Grid.SetColumn(status, 2);
        grid.Children.Add(old); grid.Children.Add(after); grid.Children.Add(status);
        return new Border
        {
            Name = "PreviewRow", Tag = item, Child = grid, BorderBrush = RowLineBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Background = bad ? BadRowBrush : Brushes.Transparent
        };
    }

    // ------------------------------------------------------------------ behaviour

    private void Wire()
    {
        Editor.TemplateChanged += OnChanged;
        foreach (var box in new[] { FindBox, ReplaceBox, CustomDateBox }) box.TextChanged += (_, _) => OnChanged();
        foreach (var box in new[] { StartBox, RemoveFirstBox, RemoveLastBox })
            box.TextChanged += (_, _) =>
            {
                var digits = new string((box.Text ?? "").Where(char.IsAsciiDigit).Take(9).ToArray());
                if (digits != (box.Text ?? "")) { box.Text = digits; return; }
                OnChanged();
            };
        foreach (var combo in new[] { LookCombo, CapitalsCombo, ExtensionCombo, SortCombo }) combo.SelectionChanged += (_, _) => OnChanged();
        DateFormatCombo.SelectionChanged += (_, _) =>
        {
            CustomDateBox.IsVisible = DateFormatCombo.SelectedIndex >= RenameDateFormats.Presets.Length;
            OnChanged();
        };
        foreach (var check in new[] { RemoveSpacesCheck, RemoveDigitsCheck, MatchCaseCheck, RegexCheck }) check.IsCheckedChanged += (_, _) => OnChanged();
        MoreToggle.Click += (_, _) => SetMoreOptions(!MoreCard.IsVisible);
        RenameButton.Click += async (_, _) => await RenameAsync();
        UndoButton.Click += async (_, _) => await UndoAsync();
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>Shows or hides the "More options" card (collapsed when the dialog opens).</summary>
    public void SetMoreOptions(bool open)
    {
        MoreCard.IsVisible = open;
        MoreToggle.Content = open ? "▾ More options" : "▸ More options";
    }

    private static int ParseInt(TextBox box, int fallback) => int.TryParse(box.Text, out var n) ? n : fallback;

    private RenameRecipe ReadRecipe()
    {
        var dateIndex = DateFormatCombo.SelectedIndex;
        var dateFormat = dateIndex >= 0 && dateIndex < RenameDateFormats.Presets.Length
            ? RenameDateFormats.Presets[dateIndex]
            : string.IsNullOrWhiteSpace(CustomDateBox.Text) ? RenameRecipe.DefaultDateFormat : CustomDateBox.Text!;
        return new RenameRecipe
        {
            Template = Editor.Template,
            Find = FindBox.Text ?? "",
            Replace = ReplaceBox.Text ?? "",
            MatchCase = MatchCaseCheck.IsChecked == true,
            UseRegex = RegexCheck.IsChecked == true,
            NumberStart = ParseInt(StartBox, 1),
            NumberDigits = Math.Max(1, LookCombo.SelectedIndex + 1),
            Case = (RenameCase)Math.Max(0, CapitalsCombo.SelectedIndex),
            DateFormat = dateFormat,
            RemoveFirst = ParseInt(RemoveFirstBox, 0),
            RemoveLast = ParseInt(RemoveLastBox, 0),
            RemoveSpaces = RemoveSpacesCheck.IsChecked == true,
            RemoveDigits = RemoveDigitsCheck.IsChecked == true,
            Extension = (RenameExtension)Math.Max(0, ExtensionCombo.SelectedIndex),
            Sort = (RenameSort)Math.Max(0, SortCombo.SelectedIndex),
        }.Normalized();
    }

    /// <summary>Puts every box into the state <paramref name="recipe"/> describes (a loaded preset).</summary>
    public void ApplyRecipe(RenameRecipe recipe)
    {
        var r = recipe.Normalized();
        _loading = true;
        try
        {
            Editor.Template = r.Template;
            FindBox.Text = r.Find;
            ReplaceBox.Text = r.Replace;
            MatchCaseCheck.IsChecked = r.MatchCase;
            RegexCheck.IsChecked = r.UseRegex;
            StartBox.Text = r.NumberStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LookCombo.SelectedIndex = Math.Clamp(r.NumberDigits, 1, 4) - 1;
            CapitalsCombo.SelectedIndex = (int)r.Case;
            var preset = Array.IndexOf(RenameDateFormats.Presets, r.DateFormat);
            DateFormatCombo.SelectedIndex = preset >= 0 ? preset : RenameDateFormats.Presets.Length;
            CustomDateBox.Text = preset >= 0 ? "" : r.DateFormat;
            CustomDateBox.IsVisible = preset < 0;
            RemoveFirstBox.Text = r.RemoveFirst.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RemoveLastBox.Text = r.RemoveLast.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RemoveSpacesCheck.IsChecked = r.RemoveSpaces;
            RemoveDigitsCheck.IsChecked = r.RemoveDigits;
            ExtensionCombo.SelectedIndex = (int)r.Extension;
            SortCombo.SelectedIndex = (int)r.Sort;
            // Settings that are not at their defaults are worth showing.
            if (r.DateFormat != RenameRecipe.DefaultDateFormat || r.RemoveFirst > 0 || r.RemoveLast > 0 || r.RemoveSpaces || r.RemoveDigits
                || r.Extension != RenameExtension.Keep || r.Sort != RenameSort.CurrentOrder || r.MatchCase || r.UseRegex)
                SetMoreOptions(true);
        }
        finally { _loading = false; }
        OnChanged();
    }

    // ------------------------------------------------------------------ presets

    /// <summary>Saves the boxes as they are under <paramref name="name"/> (a preset of that name is replaced). False when the name is blank.</summary>
    public bool SavePreset(string? name)
    {
        var clean = RenamePresets.CleanName(name);
        if (clean.Length == 0) return false;
        _presets = RenamePresets.Save(_presets, clean, ReadRecipe());
        _currentPreset = clean;
        UpdatePresetsButton();
        PresetsChanged?.Invoke(RenamePresets.Serialize(_presets));
        return true;
    }

    /// <summary>Fills the boxes from the preset <paramref name="name"/>. False when there is none.</summary>
    public bool LoadPreset(string? name)
    {
        var preset = RenamePresets.Find(_presets, name);
        if (preset is null) return false;
        _currentPreset = preset.Name;
        ApplyRecipe(preset.Recipe);
        UpdatePresetsButton();
        return true;
    }

    public void DeletePreset(string? name)
    {
        _presets = RenamePresets.Delete(_presets, name);
        if (string.Equals(_currentPreset, RenamePresets.CleanName(name), StringComparison.OrdinalIgnoreCase)) _currentPreset = null;
        UpdatePresetsButton();
        PresetsChanged?.Invoke(RenamePresets.Serialize(_presets));
    }

    private void UpdatePresetsButton() =>
        PresetsButton.Content = _currentPreset is null ? "Saved  ▾" : "Saved: " + _currentPreset + "  ▾";

    // ------------------------------------------------------------------ live preview

    /// <summary>The boxes changed: plan again, at once for a short list whose dates are known, otherwise after a pause and off the UI thread.</summary>
    private void OnChanged()
    {
        if (_loading) return;
        _messageOverride = null;
        var recipe = ReadRecipe();
        _generation++;
        _planCancel?.Cancel();
        _planCancel = null;
        var ready = Facts.IsReady(_sources, recipe.NeedsTaken, recipe.NeedsModified, recipe.NeedsSize);
        if (ready && _sources.Count <= SyncLimit)
        {
            _debounce.Stop();
            ApplyPlan(RenamePlanner.Plan(_sources, recipe, Facts.Get, _pathExists));
            NotifyIdle();
            return;
        }
        _pendingRecipe = recipe;
        RenameButton.IsEnabled = false;
        if (!ready) StatusText.Text = "Reading dates…";
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunInBackgroundAsync(RenameRecipe recipe, int generation)
    {
        _planCancel?.Cancel();
        var cancel = _planCancel = new CancellationTokenSource();
        var token = cancel.Token;
        Interlocked.Increment(ref _inFlight);
        try
        {
            var plan = await Task.Run(() =>
            {
                Facts.Load(_sources, recipe.NeedsTaken, recipe.NeedsModified, recipe.NeedsSize, token);
                token.ThrowIfCancellationRequested();
                return RenamePlanner.Plan(_sources, recipe, Facts.Get, _pathExists);
            }, token);
            if (generation == _generation && !token.IsCancellationRequested) ApplyPlan(plan);
        }
        catch (OperationCanceledException) { /* a newer change took over */ }
        catch (Exception ex)
        {
            StatusText.Text = "Could not prepare the preview: " + ex.Message;
            StatusText.Foreground = BadBrush;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            NotifyIdle();
        }
    }

    /// <summary>A task that completes when no preview is waiting to be planned (tests await it).</summary>
    internal Task WhenPreviewReadyAsync()
    {
        if (_inFlight == 0 && !_debounce.IsEnabled) return Task.CompletedTask;
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _idleWaiters.Add(waiter);
        return waiter.Task;
    }

    private void NotifyIdle()
    {
        if (_inFlight != 0 || _debounce.IsEnabled) return;
        foreach (var waiter in _idleWaiters) waiter.TrySetResult();
        _idleWaiters.Clear();
    }

    private void ApplyPlan(BatchRenamePlan plan)
    {
        CurrentPlan = plan;
        PreviewList.ItemsSource = plan.Items;
        UpdateStatus();
        RenameButton.IsEnabled = !_renaming && plan.ExpressionError is null && plan.WillRenameCount > 0;
        RenameButton.Content = plan.WillRenameCount > 0 ? $"Rename {plan.WillRenameCount}" : "Rename";
    }

    private void UpdateStatus()
    {
        var plan = CurrentPlan;
        string text;
        var bad = false;
        if (_messageOverride is not null) { text = _messageOverride; bad = true; }
        else if (plan.ExpressionError is not null) { text = plan.ExpressionError; bad = true; }
        else
        {
            var will = plan.WillRenameCount;
            var problems = plan.ProblemCount;
            text = will == 0 && problems == 0 ? "Nothing will change yet" : $"{will} will be renamed";
            if (problems > 0) text += problems == 1 ? " · 1 problem" : $" · {problems} problems";
        }
        StatusText.Text = text;
        StatusText.Foreground = bad ? BadBrush : DimBrush;
        ToolTip.SetTip(StatusText, text);
    }

    // ------------------------------------------------------------------ rename and undo

    private async Task RenameAsync()
    {
        if (_renaming || CurrentPlan.ExpressionError is not null || CurrentPlan.WillRenameCount == 0 || _debounce.IsEnabled || _inFlight != 0) return;
        await RunMovesAsync(CurrentPlan.SafeMoves, isUndo: false);
    }

    private async Task UndoAsync()
    {
        if (_renaming || _lastBatch is null) return;
        var why = RenameUndo.Check(_lastBatch, _pathExists);
        if (why is not null) { _messageOverride = why; UpdateStatus(); return; }
        await RunMovesAsync(RenameUndo.Invert(_lastBatch), isUndo: true);
    }

    private async Task RunMovesAsync(IReadOnlyList<(string From, string To)> moves, bool isUndo)
    {
        _renaming = true;
        RenameButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        var executor = Executor;
        BatchRenameResult result;
        try { result = await Task.Run(() => executor(moves)); }
        catch (Exception ex) { result = new BatchRenameResult(false, [], "Renaming failed: " + ex.Message); }
        _renaming = false;
        if (!result.Success)
        {
            // The folder may have changed under us: check the names again, then say why nothing happened.
            OnChanged();
            _messageOverride = result.Error ?? "Nothing was renamed.";
            UpdateStatus();
            UpdateUndoButton();
            return;
        }
        Close(new BatchRenameDialogResult(result.Renamed, isUndo));
    }

    internal void UpdateUndoButton()
    {
        var why = _lastBatch is null ? "There is nothing to undo yet: this brings back the names from the last rename." : RenameUndo.Check(_lastBatch, _pathExists);
        UndoButton.IsEnabled = !_renaming && _lastBatch is not null && why is null;
        ToolTip.SetTip(UndoButton, why ?? "Put the names from the last rename back");
    }

    /// <summary>Test hook: the rename the Rename button would run.</summary>
    internal Task RenameForTestsAsync() => RenameAsync();

    /// <summary>Test hook: the undo the Undo button would run.</summary>
    internal Task UndoForTestsAsync() => UndoAsync();
}
