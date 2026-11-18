using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>Headless checks of the template-less Settings card control (no real UI driving).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingsCardControlTests
{
    public SettingsCardControlTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    /// <summary>Builds a card the way the XAML loader does and hosts it in a Window, the rooted logical
    /// tree that makes Avalonia run OnInitialized (the body wrapping).</summary>
    private static (Window Window, SettingsCard Card, StackPanel Body, CheckBox Row) CreateCard(bool expanded = true)
    {
        var row = new CheckBox { Name = "RowCheck", Content = "Some setting" };
        var body = new StackPanel { Spacing = 10 };
        body.Classes.Add(SettingsCard.BodyClass);
        body.Children.Add(row);
        var card = new SettingsCard();
        // Mirror the XAML loader: properties and child are set between BeginInit and EndInit.
        card.BeginInit();
        card.CardId = "test-card";
        card.Title = "Test card";
        card.Subtitle = "What this card holds";
        card.Icon = "Info";
        card.IsExpanded = expanded;
        card.Child = body;
        card.EndInit();
        var window = new Window { Content = card };
        window.Show();
        return (window, card, body, row);
    }

    [Fact]
    public void Body_child_is_wrapped_below_the_header()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var (window, card, body, row) = CreateCard();
            Assert.Same(body, card.Body);
            Assert.Contains(SettingsCard.CardClass, card.Classes);
            Assert.Contains(SettingsCard.HeaderClass, card.Header.Classes);
            var root = Assert.IsType<StackPanel>(card.Child);
            Assert.Same(card.Header, root.Children[0]);
            Assert.Same(body, root.Children[^1]);
            Assert.Same(row, card.GetLogicalDescendants().OfType<CheckBox>().Single());
            Assert.Equal("Test card", Avalonia.Automation.AutomationProperties.GetName(card.Header));
            Assert.Equal("What this card holds", Avalonia.Automation.AutomationProperties.GetHelpText(card.Header));
            window.Close();
        });
    }

    [Fact]
    public void Body_without_card_body_class_throws()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var card = new SettingsCard();
            card.BeginInit();
            card.Child = new StackPanel();
            card.EndInit();
            var window = new Window();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { window.Content = card; window.Show(); });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Collapsing_hides_the_body_but_keeps_rows_in_the_trees()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var (window, card, body, row) = CreateCard(expanded: false);
            try
            {
                Assert.False(body.IsVisible);
                Assert.False(card.Header.IsChecked);
                Assert.Same(row, card.GetLogicalDescendants().OfType<CheckBox>().Single());
                Assert.Same(body, row.GetVisualParent());
                Assert.Same(card, row.GetLogicalAncestors().OfType<SettingsCard>().First());

                var raised = 0;
                card.ExpandedChanged += (_, _) => raised++;
                card.IsExpanded = true;
                Assert.True(body.IsVisible);
                Assert.True(card.Header.IsChecked);
                Assert.Equal(1, raised);

                card.Header.IsChecked = false;
                Assert.False(card.IsExpanded);
                Assert.False(body.IsVisible);
                Assert.Equal(2, raised);
                Assert.Same(row, card.GetLogicalDescendants().OfType<CheckBox>().Single());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ChangedCount_drives_the_badge_and_automation_name()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var (window, card, _, _) = CreateCard();
            Assert.False(card.Badge.IsVisible);

            card.ChangedTip = "Some setting";
            card.ChangedCount = 3;
            Assert.True(card.Badge.IsVisible);
            Assert.Equal("3 changed", Assert.IsType<TextBlock>(card.Badge.Child).Text);
            Assert.Equal("Some setting", ToolTip.GetTip(card.Badge));
            Assert.Equal("Test card, 3 changed", Avalonia.Automation.AutomationProperties.GetName(card.Header));

            card.ChangedCount = 0;
            Assert.False(card.Badge.IsVisible);
            Assert.Null(ToolTip.GetTip(card.Badge));
            Assert.Equal("Test card", Avalonia.Automation.AutomationProperties.GetName(card.Header));
            window.Close();
        });
    }

    [Fact]
    public void SettingIds_attached_property_round_trips()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var anchor = new Border();
            Assert.Null(SettingsCard.GetSettingIds(anchor));
            Assert.Empty(SettingsCard.GetSettingIdList(anchor));
            SettingsCard.SetSettingIds(anchor, "profiles.import, profiles.export,");
            Assert.Equal("profiles.import, profiles.export,", SettingsCard.GetSettingIds(anchor));
            Assert.Equal(new[] { "profiles.import", "profiles.export" }, SettingsCard.GetSettingIdList(anchor));
        });
    }

    [Fact]
    public void Header_toggles_with_enter_and_space()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var (window, card, body, _) = CreateCard();
            try
            {
                Assert.True(card.Header.Focusable);
                Assert.True(card.Header.IsTabStop);

                // Real keyboard input through the headless input pipeline to the focused header.
                Assert.True(card.Header.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.False(card.IsExpanded);
                Assert.False(body.IsVisible);

                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.True(card.IsExpanded);
                Assert.True(body.IsVisible);
                Assert.True(card.Header.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
