using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.Core.Tags;

namespace Glide.App.Controls;

/// <summary>
/// The tag sidebar (issue #4): the tag groups as tick boxes that mirror the tags already in the selected files' names
/// (ticked: all of them have it, dash: some do). Clicking a tag asks for it to be added (or removed when every selected file
/// has it) at once, or, with "apply immediately" off, stages it until Add / Remove is pressed. The control only asks
/// (<see cref="ChangeRequested"/>); the window renames the files and then calls <see cref="SetTargets"/> again.
/// </summary>
internal sealed class TagSidebar : Border
{
    public const double PanelWidth = 264;

    private TagOptions _options = TagOptions.Create("", "", "", "", "", "");
    private IReadOnlyList<string> _names = [];
    private string _caption = "";
    private bool _applyImmediately = true;
    private readonly HashSet<string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _groupsHost = new() { Spacing = 2 };
    private readonly TextBlock _captionText = new() { FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, Name = "TagSidebarCaption" };
    private readonly List<CheckBox> _tagBoxes = new();

    public CheckBox ApplyImmediatelyCheck { get; } = new() { Name = "TagApplyImmediately", Content = "Apply tag clicks immediately" };
    public Button AddButton { get; } = new() { Name = "TagAddButton", Content = "Add", MinWidth = 80 };
    public Button RemoveButton { get; } = new() { Name = "TagRemoveButton", Content = "Remove", MinWidth = 80 };
    public Button CloseButton { get; } = new() { Name = "TagCloseButton", Content = "✕", Width = 28, Height = 28, Padding = new Thickness(0), Focusable = false };
    public Button SettingsButton { get; } = new() { Name = "TagSettingsButton", Content = "Tag settings…", HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>The tag tick boxes, in display order (the Tag of each is the tag text).</summary>
    public IReadOnlyList<CheckBox> TagBoxes => _tagBoxes;

    /// <summary>Tags and the files' names changed: (tags to add, tags to remove). The files to change are the ones given to <see cref="SetTargets"/>.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? ChangeRequested;
    public event Action? CloseRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? ApplyImmediatelyChanged;

    public TagSidebar()
    {
        Name = "TagSidebar";
        Width = PanelWidth;
        BorderThickness = new Thickness(1, 0, 0, 0);
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushChrome"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));

        var title = new TextBlock { Text = "Tags", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(CloseButton, 1);
        header.Children.Add(CloseButton);
        ToolTip.SetTip(CloseButton, "Hide the tag sidebar");

        var scroller = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _groupsHost, Padding = new Thickness(0, 0, 8, 0) };
        var stagedButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Name = "TagStagedButtons", Children = { AddButton, RemoveButton } };
        var footer = new StackPanel { Spacing = 8, Children = { ApplyImmediatelyCheck, stagedButtons, SettingsButton } };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(14, 10, 8, 12), RowSpacing = 8 };
        root.Children.Add(header);
        Grid.SetRow(_captionText, 1); root.Children.Add(_captionText);
        Grid.SetRow(scroller, 2); root.Children.Add(scroller);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Child = root;

        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        ApplyImmediatelyCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _applyImmediately = ApplyImmediatelyCheck.IsChecked == true;
            _staged.Clear();
            Rebuild();
            ApplyImmediatelyChanged?.Invoke(_applyImmediately);
        };
        AddButton.Click += (_, _) => ApplyStaged(add: true);
        RemoveButton.Click += (_, _) => ApplyStaged(add: false);
        Rebuild();
    }

    private bool _loading;

    /// <summary>The tag groups and the immediate / staged mode (from Settings).</summary>
    public void Configure(TagOptions options, bool applyImmediately)
    {
        _options = options;
        _applyImmediately = applyImmediately;
        _staged.Clear();
        _loading = true;
        ApplyImmediatelyCheck.IsChecked = applyImmediately;
        _loading = false;
        Rebuild();
    }

    /// <summary>The files the sidebar works on (their names, without folders) and a short description of them ("3 pictures selected").</summary>
    public void SetTargets(IReadOnlyList<string> fileNames, string caption)
    {
        if (!_names.SequenceEqual(fileNames)) _staged.Clear();
        _names = fileNames;
        _caption = caption;
        Rebuild();
    }

    /// <summary>Tags ticked and waiting for Add / Remove (only with "apply immediately" off).</summary>
    public IReadOnlyCollection<string> StagedTags => _staged;

    private void Rebuild()
    {
        _groupsHost.Children.Clear();
        _tagBoxes.Clear();
        var any = _names.Count > 0;
        _captionText.Text = any ? _caption : "Select one or more pictures to tag them.";
        AddButton.IsVisible = RemoveButton.IsVisible = !_applyImmediately;
        AddButton.IsEnabled = RemoveButton.IsEnabled = any && _staged.Count > 0;
        ((Panel)AddButton.Parent!).IsVisible = !_applyImmediately;

        if (_options.Groups.Count == 0)
        {
            _groupsHost.Children.Add(new TextBlock
            {
                Name = "TagEmptyText", TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 6),
                Text = "No tags yet. Add tag groups in Settings > Window & navigation > Tags."
            });
            return;
        }

        foreach (var group in _options.Groups)
        {
            var header = new StackPanel { Margin = new Thickness(0, 8, 0, 2) };
            header.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeight.SemiBold, Name = "TagGroupName" });
            if (group.IncludeGroupTag)
                header.Children.Add(new TextBlock { Text = $"also adds \"{group.Name}\"", FontSize = 11, Opacity = .6 });
            _groupsHost.Children.Add(header);
            foreach (var tag in group.Tags) _groupsHost.Children.Add(BuildTagBox(tag, any));
        }
    }

    private CheckBox BuildTagBox(string tag, bool enabled)
    {
        var presence = TagFileName.PresenceIn(_names, tag, _options);
        var box = new CheckBox { Tag = tag, IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0) };
        if (_applyImmediately)
        {
            box.Content = new TextBlock { Text = tag };   // a TextBlock: a string would read "_" as an access key (sibling_1)
            box.IsChecked = presence switch { TagPresence.All => true, TagPresence.Some => null, _ => false };
        }
        else
        {
            // Staged mode: the box is the staged choice; a marker shows what the files already have.
            var marker = presence switch { TagPresence.All => "●  ", TagPresence.Some => "◐  ", _ => "" };
            box.Content = new TextBlock { Text = marker + tag };
            box.IsChecked = _staged.Contains(tag);
        }
        box.Click += (_, _) => OnTagClicked(tag, presence);
        _tagBoxes.Add(box);
        return box;
    }

    private void OnTagClicked(string tag, TagPresence presence)
    {
        if (_names.Count == 0) return;
        if (_applyImmediately)
        {
            // The click flipped the box; the host renames and then calls SetTargets, so the files decide the real state again.
            if (presence == TagPresence.All) ChangeRequested?.Invoke([], [tag]);
            else ChangeRequested?.Invoke([tag], []);
            return;
        }
        if (!_staged.Remove(tag)) _staged.Add(tag);
        AddButton.IsEnabled = RemoveButton.IsEnabled = _staged.Count > 0;
    }

    private void ApplyStaged(bool add)
    {
        if (_staged.Count == 0 || _names.Count == 0) return;
        var tags = _staged.ToArray();
        _staged.Clear();
        ChangeRequested?.Invoke(add ? tags : [], add ? [] : tags);
        Rebuild();   // the host also calls SetTargets after the rename; this clears the ticks at once
    }
}
