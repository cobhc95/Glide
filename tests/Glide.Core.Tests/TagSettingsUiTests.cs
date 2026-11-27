using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Glide.App.Settings;
using Glide.Core.Tags;

namespace Glide.Core.Tests;

/// <summary>Issue #4: the Settings > Tags card loads the groups into editable rows and reads them back.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class TagSettingsUiTests
{
    public TagSettingsUiTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static GlideSettingsState Read(SettingsWindow window) =>
        (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;

    [Fact]
    public void Groups_prefix_brackets_and_format_load_and_read_back() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var state = new GlideSettingsState
        {
            TagsEnabled = true, TagsApplyImmediately = false, TagGroups = "+Family: parents, mom\nFriends: anna",
            TagPrefix = "#", TagOpen = "[", TagClose = "]", TagSeparator = ", ", TagFilenameFormat = "{tags} {name}"
        };
        var window = new SettingsWindow(state, (_, _) => { });
        var host = window.FindControl<StackPanel>("TagGroupsHost")!;
        Assert.Equal(2, host.Children.Count);
        Assert.Equal("#", window.FindControl<TextBox>("TagPrefixBox")!.Text);
        Assert.Equal("[", window.FindControl<TextBox>("TagOpenBox")!.Text);
        Assert.Contains("[#Family, #parents, #mom]", window.FindControl<TextBlock>("TagExampleText")!.Text);

        var read = Read(window);
        Assert.True(read.TagsEnabled);
        Assert.False(read.TagsApplyImmediately);
        Assert.Equal("+Family: parents, mom\nFriends: anna", read.TagGroups);
        Assert.Equal(("#", "[", "]", ", ", "{tags} {name}"), (read.TagPrefix, read.TagOpen, read.TagClose, read.TagSeparator, read.TagFilenameFormat));
    });

    [Fact]
    public void Adding_editing_moving_and_removing_group_rows_changes_what_is_saved() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
        Assert.Equal("", Read(window).TagGroups);
        var host = window.FindControl<StackPanel>("TagGroupsHost")!;
        window.FindControl<Button>("TagAddGroupButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FindControl<Button>("TagAddGroupButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var rows = host.Children.OfType<Grid>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("", Read(window).TagGroups);   // nameless rows are not saved

        void Fill(Grid row, string name, string tags, bool include)
        {
            row.Children.OfType<CheckBox>().Single().IsChecked = include;
            var boxes = row.Children.OfType<TextBox>().ToList();
            boxes[0].Text = name; boxes[1].Text = tags;
        }
        Fill(rows[0], "Family", "mom, dad", include: true);
        Fill(rows[1], "Friends", "anna,ben", include: false);
        Assert.Equal("+Family: mom, dad\nFriends: anna, ben", Read(window).TagGroups);

        // Down on the first row swaps the two groups (the buttons are the last child of the row).
        var buttons = rows[0].Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToList();
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Friends: anna, ben\n+Family: mom, dad", Read(window).TagGroups);

        rows[0].Children.OfType<StackPanel>().Single().Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));   // remove (the moved Family row is last)
        Assert.Equal("Friends: anna, ben", Read(window).TagGroups);
        Assert.Single(TagGroupCatalog.Parse(Read(window).TagGroups));
    });
}
