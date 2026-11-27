using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Glide.Core.Settings;
using Glide.Core.Tags;

namespace Glide.App.Settings;

/// <summary>Settings > Window &amp; navigation > Tags: the tag group editor and the tag block options (issue #4).</summary>
public partial class SettingsWindow
{
    /// <summary>One editor row: include-group-tag box, group name and comma separated tags.</summary>
    private sealed record TagGroupRow(CheckBox Include, TextBox Name, TextBox Tags);

    private readonly List<TagGroupRow> _tagGroupRows = new();

    private void LoadTagControls(GlideSettingsState s)
    {
        TagGroupsHost.Children.Clear();
        _tagGroupRows.Clear();
        foreach (var group in TagGroupCatalog.Parse(s.TagGroups))
            AddTagGroupRow(group.Name, group.IncludeGroupTag, string.Join(", ", group.Tags));
        TagPrefixBox.Text = s.TagPrefix;
        TagOpenBox.Text = s.TagOpen;
        TagCloseBox.Text = s.TagClose;
        TagSeparatorBox.Text = s.TagSeparator;
        TagFormatBox.Text = s.TagFilenameFormat;
        UpdateTagExample();
    }

    private void ReadTagControls(GlideSettingsState state)
    {
        var groups = _tagGroupRows
            .Select(r => new TagGroup((r.Name.Text ?? "").Trim().Replace(":", ""), r.Include.IsChecked == true,
                (r.Tags.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .Where(g => g.Name.Length > 0)
            .ToList();
        state.TagGroups = TagGroupCatalog.Serialize(groups);
        state.TagPrefix = TagPrefixBox.Text?.Trim() ?? "";
        // An empty bracket / separator / format is not an option: it falls back to the default (also in TagOptions).
        state.TagOpen = string.IsNullOrEmpty(TagOpenBox.Text) ? "{" : TagOpenBox.Text;
        state.TagClose = string.IsNullOrEmpty(TagCloseBox.Text) ? "}" : TagCloseBox.Text;
        state.TagSeparator = string.IsNullOrEmpty(TagSeparatorBox.Text) ? ";" : TagSeparatorBox.Text;
        state.TagFilenameFormat = string.IsNullOrWhiteSpace(TagFormatBox.Text) ? "{name} {tags}" : TagFormatBox.Text;
    }

    private void WireTagControls()
    {
        foreach (var box in new[] { TagPrefixBox, TagOpenBox, TagCloseBox, TagSeparatorBox, TagFormatBox })
            box.TextChanged += (_, _) => UpdateTagExample();
    }

    private void TagAddGroupClicked(object? sender, RoutedEventArgs e)
    {
        AddTagGroupRow("", includeGroupTag: false, "");
        _tagGroupRows[^1].Name.Focus();
        NotifyTagGroupsChanged();
    }

    private void AddTagGroupRow(string name, bool includeGroupTag, string tags)
    {
        var include = new CheckBox { Content = "Include group tag", IsChecked = includeGroupTag, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(include, "Also write the group's name as a tag whenever one of its tags is added (and remove it with the last one).");
        var nameBox = new TextBox { Text = name, Width = 150, Watermark = "Group name", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var tagsBox = new TextBox { Text = tags, Watermark = "tag, tag, tag (in this order)", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var up = new Button { Content = "↑", Width = 30 };
        var down = new Button { Content = "↓", Width = 30 };
        var remove = new Button { Content = "✕", Width = 30 };
        ToolTip.SetTip(up, "Move the group up");
        ToolTip.SetTip(down, "Move the group down");
        ToolTip.SetTip(remove, "Remove the group");
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, Children = { up, down, remove } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(include);
        Grid.SetColumn(nameBox, 1); row.Children.Add(nameBox);
        Grid.SetColumn(tagsBox, 2); row.Children.Add(tagsBox);
        Grid.SetColumn(buttons, 3); row.Children.Add(buttons);
        var entry = new TagGroupRow(include, nameBox, tagsBox);
        row.Tag = entry;
        TagGroupsHost.Children.Add(row);
        _tagGroupRows.Add(entry);

        include.IsCheckedChanged += (_, _) => NotifyTagGroupsChanged();
        nameBox.TextChanged += (_, _) => NotifyTagGroupsChanged();
        tagsBox.TextChanged += (_, _) => NotifyTagGroupsChanged();
        remove.Click += (_, _) => { TagGroupsHost.Children.Remove(row); _tagGroupRows.Remove(entry); NotifyTagGroupsChanged(); };
        up.Click += (_, _) => MoveTagGroupRow(row, -1);
        down.Click += (_, _) => MoveTagGroupRow(row, 1);
    }

    private void MoveTagGroupRow(Grid row, int delta)
    {
        var index = TagGroupsHost.Children.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= TagGroupsHost.Children.Count) return;
        TagGroupsHost.Children.RemoveAt(index);
        TagGroupsHost.Children.Insert(target, row);
        _tagGroupRows.Clear();
        foreach (var child in TagGroupsHost.Children.OfType<Grid>())
            if (child.Tag is TagGroupRow r) _tagGroupRows.Add(r);
        NotifyTagGroupsChanged();
    }

    /// <summary>The group rows are not catalogue controls, so they tell the Settings window themselves that something changed.</summary>
    private void NotifyTagGroupsChanged()
    {
        if (_loadingControls) return;
        UpdateTagExample();
        ScheduleDebouncedLivePreview();
    }

    private void UpdateTagExample()
    {
        var options = TagOptions.Create(
            string.Join("\n", _tagGroupRows.Select(r => (r.Include.IsChecked == true ? "+" : "") + (r.Name.Text ?? "").Replace(":", "") + ": " + r.Tags.Text)),
            TagPrefixBox.Text, TagOpenBox.Text, TagCloseBox.Text, TagSeparatorBox.Text, TagFormatBox.Text);
        var sample = options.Groups.SelectMany(g => g.Tags).Take(2).ToArray();
        if (sample.Length == 0) sample = ["sea", "sun"];
        TagExampleText.Text = "Example: " + TagFileName.AddTags("Holiday 0042.jpg", sample, options);
    }
}
