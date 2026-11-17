using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.6 report: with "Always expand" on, the first page Settings opens on (General &amp; startup) showed every card
/// collapsed, with Expand all enabled and Collapse all greyed out; switching pages expanded them. Always expand was only
/// applied by ShowCategory, which the constructor never runs for the initial page, and a state load (Restore defaults,
/// a profile, a backup) that turned the switch on was skipped by the loading guard.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsAlwaysExpandTests
{
    public SettingsAlwaysExpandTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => (c as StyledElement)?.Name == name);

    private static void AssertPageExpanded(SettingsWindow window, string panel)
    {
        var page = Named<StackPanel>(window, panel);
        Assert.True(page.IsVisible, $"{panel} is not the visible page");
        var cards = page.GetLogicalDescendants().OfType<SettingsCard>().ToList();
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.True(c.IsExpanded, $"{panel}: card '{c.CardId}' is collapsed"));
        Assert.False(Named<Button>(window, "ExpandAllCardsButton").IsEnabled, "Expand all should be disabled: everything is expanded");
        Assert.True(Named<Button>(window, "CollapseAllCardsButton").IsEnabled, "Collapse all should be enabled");
    }

    [Fact]
    public void Always_expand_opens_the_first_page_expanded_and_again_after_a_reopen()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            for (var round = 0; round < 2; round++)
            {
                var window = new SettingsWindow(new GlideSettingsState { SettingsAlwaysExpand = true }, (_, _) => { }) { Width = 930, Height = 700 };
                window.Show();
                try
                {
                    Settle(window);
                    Assert.True(Named<Avalonia.Controls.Primitives.ToggleButton>(window, "SettingsAlwaysExpandSwitch").IsChecked);
                    AssertPageExpanded(window, "GeneralPanel");
                }
                finally { window.Close(); }
            }
        });
    }

    [Fact]
    public void Always_expand_applies_to_a_page_opened_by_a_command_on_a_new_window()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState { SettingsAlwaysExpand = true }, (_, _) => { }) { Width = 930, Height = 700 };
            window.Opened += (_, _) => window.RevealSettingFromCommand("tabs.switchToNewTabs");
            window.Show();
            try
            {
                Settle(window);
                AssertPageExpanded(window, "TabsPanel");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void A_state_load_that_turns_always_expand_on_expands_the_visible_page()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                Settle(window);
                Assert.All(Named<StackPanel>(window, "GeneralPanel").GetLogicalDescendants().OfType<SettingsCard>(), c => Assert.False(c.IsExpanded));
                // A profile / backup / Restore defaults loads a state with Always expand on.
                typeof(SettingsWindow).GetMethod("LoadControls", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [new GlideSettingsState { SettingsAlwaysExpand = true }]);
                Settle(window);
                AssertPageExpanded(window, "GeneralPanel");
            }
            finally { window.Close(); }
        });
    }
}
