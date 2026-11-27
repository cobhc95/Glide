using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.7 Settings window items, driven through the real SettingsWindow controls (docs/audits/5.1.7-test-coverage.md):
/// the shared-key note, the Hide cursor link, the unsaved-changes prompt with choice labels, and the text overlay look
/// cards. Nothing is saved: the window gets a callback that only records.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class Release517SettingsUiTests : IDisposable
{
    public Release517SettingsUiTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly List<IStyle> InstalledStyles = new();

    private static void EnsureAppStyles()
    {
        if (InstalledStyles.Count > 0) return;
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        InstalledStyles.Add(new FluentTheme());
        InstalledStyles.Add(new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
        for (var i = 0; i < InstalledStyles.Count; i++) app.Styles.Insert(i, InstalledStyles[i]);
    }

    public void Dispose()
    {
        if (InstalledStyles.Count == 0) return;
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            foreach (var style in InstalledStyles) Application.Current!.Styles.Remove(style);
            InstalledStyles.Clear();
        });
    }

    private static SettingsWindow Open(string page, GlideSettingsState? state = null)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(state ?? new GlideSettingsState(), (_, _) => { });
        window.Show();
        Call(window, "ShowCategory", page, true, false, null!);
        Call(window, "ExpandAllCardsClicked", null!, new RoutedEventArgs());
        window.Width = 930; window.Height = 700;
        Settle();
        window.UpdateLayout();
        return window;
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
    }

    private static object? Call(SettingsWindow window, string name, params object[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Named<T>(SettingsWindow window, string name) where T : Visual =>
        window.GetVisualDescendants().OfType<T>().Single(c => (c as StyledElement)?.Name == name);

    private static void Choose(ComboBox combo, string text)
    {
        var index = combo.Items.Cast<object>().ToList().FindIndex(i => string.Equals((i as ComboBoxItem)?.Content as string ?? i.ToString(), text, StringComparison.Ordinal));
        Assert.True(index >= 0, $"{combo.Name} has no \"{text}\"");
        combo.SelectedIndex = index;
        Settle();
    }

    // ---- item 16: the shared-key note ------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_the_same_key_for_both_new_tab_keys_shows_the_note_and_any_other_choice_hides_it()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open("Tabs");
            try
            {
                var warning = Named<TextBlock>(window, "NewTabSharedKeyWarning");
                var position = Named<ComboBox>(window, "NewTabPositionKeyCombo");
                var foreground = Named<ComboBox>(window, "NewTabForegroundKeyCombo");
                Choose(position, "None"); Choose(foreground, "None");
                Assert.False(warning.IsVisible, "None for both is not shared");

                Choose(position, "Shift");
                Assert.False(warning.IsVisible);
                Choose(foreground, "Shift");
                Assert.True(warning.IsVisible, "the same key for both: a note says holding it inverts both");
                Assert.Contains("inverts both", warning.Text);
                // The label of the combos still names the choice; neither is refused (both stay on Shift).
                Assert.Equal("Shift", (position.SelectedItem as ComboBoxItem)?.Content);
                Assert.Equal("Shift", (foreground.SelectedItem as ComboBoxItem)?.Content);

                Choose(foreground, "Alt");
                Assert.False(warning.IsVisible);
                Choose(foreground, "Shift");
                Assert.True(warning.IsVisible);
                Choose(position, "None");
                Assert.False(warning.IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- item 17: Hide cursor lives in Mouse & keyboard, with a link to the fullscreen option -----------------------

    [Fact]
    public async Task The_hide_cursor_link_opens_the_fullscreen_hide_cursor_option_and_the_windowed_checkbox_reads_back()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open("Mouse");
            try
            {
                var windowed = Named<CheckBox>(window, "HideCursorNormalWindowCheck");
                windowed.IsChecked = true;
                Settle();
                var read = (GlideSettingsState)Call(window, "ReadControls")!;
                Assert.True(read.HideCursorNormalWindow, "ticking it on the Mouse & keyboard page changes the setting");

                var fullscreenCheck = Named<CheckBox>(window, "HideCursorFullscreenCheck");
                var link = Named<Button>(window, "HideCursorFullscreenLinkButton");
                Assert.False(fullscreenCheck.IsEffectivelyVisible, "the fullscreen option is on another page");
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                window.UpdateLayout();
                Assert.True(fullscreenCheck.IsEffectivelyVisible, "the link switched to the page holding the fullscreen option");
                Assert.True(fullscreenCheck.FindLogicalAncestorOfType<SettingsCard>()!.IsExpanded, "and expanded its card");
                Assert.False(windowed.IsEffectivelyVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- item 15: the unsaved-changes prompt names the choice labels --------------------------------------------------

    [Fact]
    public async Task Cancel_after_changing_a_choice_prompts_with_the_options_names_not_Off_to_On()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open("Tabs");
            try
            {
                IReadOnlyList<SettingChange>? prompted = null;
                window.UnsavedChangesPromptForTests = changes => { prompted = changes; return SettingsWindow.UnsavedSettingsChoice.KeepEditing; };
                Choose(Named<ComboBox>(window, "SwitchToNewTabsCombo"), "background");
                var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.NotNull(prompted);
                var change = Assert.Single(prompted!);
                Assert.Equal("foreground", change.Before);
                Assert.Equal("background", change.After);
                Assert.True(window.IsVisible, "Keep editing leaves Settings open");

                // The dialog the user sees lists "Label: old → new" with those names.
                var (dialog, _) = window.BuildUnsavedChangesDialog(prompted!);
                var row = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name is null && t.Inlines is { Count: 3 });
                var runs = row.Inlines!.OfType<Run>().Select(r => r.Text).ToList();
                Assert.Equal(change.Label + ": ", runs[0]);
                Assert.Equal("foreground → ", runs[1]);
                Assert.Equal("background", runs[2]);
                Assert.Equal("1 setting not applied", dialog.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "UnsavedChangesHeading").Text);
                Assert.DoesNotContain(runs, r => r!.Contains("Off") || r.Contains("On →"));
                dialog.Close();
            }
            finally { window.UnsavedChangesPromptForTests = null; window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- item 14: text overlay look per position (title bar / vertical tab bar) in the Settings controls ---------------

    [Fact]
    public async Task Title_bar_and_tab_bar_overlay_looks_load_into_their_own_controls_and_read_back()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState
            {
                TextOverlayTitleBarTemplate = "{stem}", TextOverlayTitleBarFontSize = 17, TextOverlayTitleBarColor = "Amber", TextOverlayTitleBarBorder = "Off",
                TextOverlayTabBarTemplate = "{index}/{total}", TextOverlayTabBarFontSize = 13, TextOverlayTabBarColor = "Accent", TextOverlayTabBarBorder = "Same as over the picture",
                TextOverlaySeparator = "|", TextOverlaySeparatorColor = "Amber"
            };
            var window = Open("Appearance", state);
            try
            {
                Assert.Equal("{stem}", Named<TextBox>(window, "TitleBarTemplateBox").Text);
                Assert.Equal(17, (int)Named<NumericUpDown>(window, "TitleBarFontSizeBox").Value!);
                Assert.Equal("{index}/{total}", Named<TextBox>(window, "TabBarTemplateBox").Text);
                Assert.Equal(13, (int)Named<NumericUpDown>(window, "TabBarFontSizeBox").Value!);

                // The user edits the title bar look only; the tab bar and the picture look stay as they were.
                Named<NumericUpDown>(window, "TitleBarFontSizeBox").Value = 0;
                Choose(Named<ComboBox>(window, "TitleBarColorCombo"), "Same as over the picture");
                Named<TextBox>(window, "TitleBarTemplateBox").Text = "{filename}";
                Settle();
                var read = (GlideSettingsState)Call(window, "ReadControls")!;
                Assert.Equal("{filename}", read.TextOverlayTitleBarTemplate);
                Assert.Equal(0, read.TextOverlayTitleBarFontSize);
                Assert.Equal("Same as over the picture", read.TextOverlayTitleBarColor);
                Assert.Equal("Off", read.TextOverlayTitleBarBorder);
                Assert.Equal("{index}/{total}", read.TextOverlayTabBarTemplate);
                Assert.Equal(13, read.TextOverlayTabBarFontSize);
                Assert.Equal("Accent", read.TextOverlayTabBarColor);
                Assert.Equal("|", read.TextOverlaySeparator);
                Assert.Equal("Amber", read.TextOverlaySeparatorColor);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
