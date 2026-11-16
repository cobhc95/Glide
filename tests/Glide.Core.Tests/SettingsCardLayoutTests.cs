using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Headless contract of the 4.8.0 Settings card layout: 8 pages of collapsible cards, the card map,
/// "N changed" badges against defaults, search reveal, Expand/Collapse all and the double load.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsCardLayoutTests
{
    public SettingsCardLayoutTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    /// <summary>Page panel name -> page key and visible title, in navigation order.</summary>
    private static readonly (string Panel, string Key, string Title)[] Pages =
    {
        ("GeneralPanel", "General", "General & startup"),
        ("AppearancePanel", "Appearance", "Appearance & viewing"),
        ("InterfacePanel", "Interface", "Window & navigation"),
        ("MousePanel", "Mouse", "Mouse & keyboard"),
        ("PerformancePanel", "Performance", "Performance"),
        ("SlideshowPanel", "Slideshow", "Slideshow & overlays"),
        ("TabsPanel", "Tabs", "Tabs & status bar"),
        ("WindowsPanel", "Windows", "Windows & developer")
    };

    // The Explorer thumbnail controls show the separately stored thumbnail settings (machine state, not
    // GlideSettingsState), so that card's badge depends on this PC and is not asserted here.
    private const string MachineDependentCard = "windows-thumbnails";

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static object? Call(SettingsWindow window, string name, params object?[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Named<T>(SettingsWindow window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static List<SettingsCard> Cards(SettingsWindow window) => window.GetLogicalDescendants().OfType<SettingsCard>().ToList();

    private static SettingsCard Card(SettingsWindow window, string id) => Cards(window).Single(c => c.CardId == id);

    private static string PageTitleOf(SettingsCard card)
    {
        var panel = card.GetLogicalAncestors().OfType<StackPanel>().First(p => Pages.Any(x => x.Panel == p.Name));
        return Pages.Single(x => x.Panel == panel.Name).Title;
    }

    private static async Task WithWindow(GlideSettingsState state, Action<SettingsWindow> body)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(state, (_, _) => { });
            window.Show();
            try { body(window); }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Every_setting_lives_in_exactly_one_card_on_the_page_named_by_its_category()
    {
        var failures = new List<string>();
        await WithWindow(new GlideSettingsState(), window =>
        {
            var controls = Field<Dictionary<string, Control>>(window, "_settingControls");
            var map = Field<Dictionary<string, SettingsCard>>(window, "_settingCard");
            var cards = Cards(window);
            var gestureCard = Card(window, "mouse-gestures");

            foreach (var setting in SettingsCatalog.All)
            {
                if (setting.Id.StartsWith("gesture.", StringComparison.Ordinal))
                {
                    if (!map.TryGetValue(setting.Id, out var g) || !ReferenceEquals(g, gestureCard))
                        failures.Add($"{setting.Id}: gesture entry is not mapped to mouse-gestures");
                    continue;
                }

                // Documented exception (tools/audit-settings-search.py outside_cards): the "Always expand" switch
                // lives in the Settings footer, per the approved mockup.
                if (setting.Id == "settings.alwaysExpand")
                {
                    if (!controls.ContainsKey(setting.Id)) failures.Add("settings.alwaysExpand: footer switch is not mapped");
                    continue;
                }
                var hosts = new HashSet<SettingsCard>();
                if (controls.TryGetValue(setting.Id, out var control))
                {
                    var owners = control.GetLogicalAncestors().OfType<SettingsCard>().ToList();
                    if (owners.Count != 1) failures.Add($"{setting.Id}: editor is inside {owners.Count} cards");
                    hosts.UnionWith(owners);
                }
                foreach (var card in cards)
                    if (card.Body.GetLogicalDescendants().OfType<Control>().Any(c => SettingsCard.GetSettingIdList(c).Contains(setting.Id)))
                        hosts.Add(card);

                if (hosts.Count != 1) { failures.Add($"{setting.Id}: placed in {hosts.Count} cards"); continue; }
                var host = hosts.Single();
                if (!map.TryGetValue(setting.Id, out var mapped) || !ReferenceEquals(mapped, host))
                    failures.Add($"{setting.Id}: card map disagrees with the tree");
                if (PageTitleOf(host) != setting.Category)
                    failures.Add($"{setting.Id}: category '{setting.Category}' but card '{host.CardId}' is on '{PageTitleOf(host)}'");
            }
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(60)));
    }

    [Fact]
    public async Task Cards_have_unique_ids_and_are_direct_children_of_the_eight_pages()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            var cards = Cards(window);
            Assert.Equal(41, cards.Count); // 41 = the 38 earlier cards + Reset Glide + Updates (5.0.1) + Back up and restore (5.0.4)
            Assert.Equal(cards.Count, cards.Select(c => c.CardId).Distinct(StringComparer.Ordinal).Count());
            foreach (var card in cards)
            {
                Assert.False(string.IsNullOrWhiteSpace(card.CardId));
                Assert.False(string.IsNullOrWhiteSpace(card.Title), card.CardId);
                Assert.False(string.IsNullOrWhiteSpace(card.Subtitle), card.CardId);
                Assert.False(string.IsNullOrWhiteSpace(card.Icon), card.CardId);
                var parent = Assert.IsType<StackPanel>(card.Parent);
                Assert.Contains(Pages, p => p.Panel == parent.Name);
            }
            foreach (var page in Pages)
                Assert.True(Named<StackPanel>(window, page.Panel).Children.OfType<SettingsCard>().Any(), $"{page.Panel} has no card");
        });
    }

    [Fact]
    public async Task Badges_count_changes_from_defaults_and_restore_defaults_clears_them()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            void AssertNoBadges(string when)
            {
                foreach (var card in Cards(window).Where(c => c.CardId != MachineDependentCard))
                    Assert.False(card.Badge.IsVisible, $"{when}: '{card.CardId}' shows a badge ({card.ChangedCount})");
            }

            AssertNoBadges("default state");

            Named<CheckBox>(window, "ShowFolderCounterCheck").IsChecked = true;
            Call(window, "UpdateDirtyState", new object?[] { null });
            var overlay = Card(window, "appearance-text-overlay");
            Assert.True(overlay.Badge.IsVisible);
            Assert.Equal("1 changed", ((TextBlock)overlay.Badge.Child!).Text);
            Assert.Contains("Show folder position line under the text overlay", overlay.ChangedTip);

            // A gesture change counts on the gesture card (even before the gesture panel is built).
            Call(window, "OnGestureSlotChosen", "wheel.windowed", GestureCatalog.ZoomWheel);
            var gestures = Card(window, "mouse-gestures");
            Assert.True(gestures.Badge.IsVisible);
            Assert.Equal(1, gestures.ChangedCount);

            // Restore defaults resets options only (Reset Glide's "All settings" scope): the option's badge clears, the
            // gesture binding is kept (it belongs to "Hotkeys and mouse gestures").
            Call(window, "RestoreDefaultsClicked", null, new RoutedEventArgs());
            Assert.False(overlay.Badge.IsVisible, "after Restore defaults the option is back to its default");
            Assert.True(gestures.Badge.IsVisible, "Restore defaults must keep mouse gestures");
            Assert.Equal(1, gestures.ChangedCount);
            foreach (var card in Cards(window).Where(c => c.CardId != MachineDependentCard && c.CardId != "mouse-gestures"))
                Assert.False(card.Badge.IsVisible, $"after Restore defaults: '{card.CardId}' shows a badge ({card.ChangedCount})");
        });
    }

    [Fact]
    public async Task Reveal_opens_the_page_expands_the_card_and_highlights_the_row()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            var card = Card(window, "appearance-text-overlay");
            card.IsExpanded = false;
            Call(window, "RevealSetting", "folderNav.showCounter");

            Assert.Equal("Appearance", Field<string>(window, "_activeCategory"));
            Assert.True(Named<StackPanel>(window, "AppearancePanel").IsVisible);
            Assert.False(Named<StackPanel>(window, "GeneralPanel").IsVisible);
            Assert.True(card.IsExpanded);
            var row = Named<CheckBox>(window, "ShowFolderCounterCheck");
            Assert.Same(card.Body, row.Parent);
            var adorner = Assert.IsAssignableFrom<Border>(AdornerLayer.GetAdorner(row));
            Assert.Contains(SettingsCard.RevealHighlightClass, adorner.Classes);
            Assert.Equal("Appearance & viewing", Named<TextBlock>(window, "PageTitle").Text);
        });
    }

    [Fact]
    public async Task Fresh_window_starts_with_every_card_collapsed()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            var cards = window.GetLogicalDescendants().OfType<SettingsCard>().ToList();
            Assert.NotEmpty(cards);
            Assert.All(cards, c => Assert.False(c.IsExpanded, c.CardId + " should start collapsed"));
            Call(window, "ShowCategory", "Appearance", true, false, null);
            Assert.False(Named<Button>(window, "CollapseAllCardsButton").IsEnabled);
            Assert.True(Named<Button>(window, "ExpandAllCardsButton").IsEnabled);
        });
    }

    [Fact]
    public async Task Default_state_is_the_Maximum_quality_profile_not_User_custom()
    {
        Assert.Equal("Maximum quality", new GlideSettingsState().InitialImageQuality);
        await WithWindow(new GlideSettingsState(), window =>
        {
            Call(window, "DetectPerformanceProfileFromControls");
            var combo = Named<ComboBox>(window, "InitialQualityCombo");
            Assert.Equal("Maximum quality", (combo.SelectedItem as ComboBoxItem)?.Content as string);
        });
    }

    [Fact]
    public async Task Card_state_survives_page_switches_and_expand_collapse_all_act_on_the_active_page_only()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            var startup = Card(window, "general-startup");
            startup.IsExpanded = true;
            Call(window, "ShowCategory", "Appearance", true, true, null);
            Call(window, "ShowCategory", "General", true, true, null);
            Assert.True(startup.IsExpanded);

            var generalBefore = Named<StackPanel>(window, "GeneralPanel").Children.OfType<SettingsCard>().Select(c => c.IsExpanded).ToList();
            Call(window, "ShowCategory", "Appearance", true, false, null);
            var appearance = Named<StackPanel>(window, "AppearancePanel").Children.OfType<SettingsCard>().ToList();
            var expandAll = Named<Button>(window, "ExpandAllCardsButton");
            var collapseAll = Named<Button>(window, "CollapseAllCardsButton");

            Call(window, "CollapseAllCardsClicked", null, new RoutedEventArgs());
            Assert.All(appearance, c => Assert.False(c.IsExpanded));
            Assert.False(collapseAll.IsEnabled);
            Assert.True(expandAll.IsEnabled);

            Call(window, "ExpandAllCardsClicked", null, new RoutedEventArgs());
            Assert.All(appearance, c => Assert.True(c.IsExpanded));
            Assert.False(expandAll.IsEnabled);
            Assert.True(collapseAll.IsEnabled);

            Assert.Equal(generalBefore, Named<StackPanel>(window, "GeneralPanel").Children.OfType<SettingsCard>().Select(c => c.IsExpanded).ToList());

            // The page buttons are hidden while search results are shown.
            Call(window, "RunSearchRebuild", "zoom");
            Assert.False(expandAll.IsVisible);
            Assert.False(collapseAll.IsVisible);
        });
    }

    [Fact]
    public async Task Navigation_search_filter_page_groups_and_catalogue_categories_agree()
    {
        await WithWindow(new GlideSettingsState(), window =>
        {
            var groups = (Array)typeof(SettingsWindow).GetField("CategoryGroups", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var groupTitles = groups.Cast<object>().Select(g => (string)g.GetType().GetProperty("Title")!.GetValue(g)!).ToList();
            var groupKeys = groups.Cast<object>().Select(g => (string)g.GetType().GetProperty("Key")!.GetValue(g)!).ToList();

            var rail = Named<StackPanel>(window, "NavigationRail").Children.OfType<Button>().ToList();
            var navTitles = rail.Select(b => b.GetLogicalDescendants().OfType<TextBlock>().Single().Text!).ToList();
            var navKeys = rail.Select(b => (string)b.Tag!).ToList();
            Assert.Equal(Pages.Select(p => p.Key), navKeys);
            Assert.Equal(Pages.Select(p => p.Title), navTitles);
            Assert.Equal(groupKeys.OrderBy(x => x), navKeys.OrderBy(x => x));
            foreach (var (key, title) in navKeys.Zip(navTitles))
                Assert.Equal(title, groupTitles[groupKeys.IndexOf(key)]);
            Assert.All(rail, b => Assert.Null(ToolTip.GetTip(b))); // category buttons have no tooltip

            var combo = Named<ComboBox>(window, "SearchCategoryCombo");
            var comboItems = combo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!).ToList();
            Assert.Equal(new[] { "All categories" }.Concat(groupTitles), comboItems);

            var categories = SettingsCatalog.All.Select(s => s.Category).Distinct().OrderBy(x => x).ToList();
            Assert.Equal(groupTitles.OrderBy(x => x), categories);
        });
    }

    [Fact]
    public async Task Opening_settings_causes_no_net_change_and_keeps_the_loaded_values()
    {
        var state = new GlideSettingsState
        {
            ShowFolderCounter = true,
            PictureCounterFontSize = 22,
            ThemeChoice = "Light",
            TabMinWidth = 150
        };
        state.Hotkeys["view.fit"] = new List<string> { "Ctrl+Shift+F9" };
        state.Gestures["wheel.windowed"] = GestureCatalog.ZoomWheel;
        state.TitleBarButtons = new List<string> { "settings", "file.open" };
        var applied = 0;

        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(state, (_, _) => applied++);
            window.Show();
            try
            {
                Assert.Equal(0, applied);
                Assert.False(Named<Button>(window, "ApplyButton").IsEnabled);
                var read = (GlideSettingsState)Call(window, "ReadControls")!;
                Assert.True(read.ContentEquals(Field<GlideSettingsState>(window, "_committed")));
                Assert.True(read.ShowFolderCounter);
                Assert.Equal(22, read.PictureCounterFontSize);
                Assert.Equal("Light", read.ThemeChoice);
                Assert.Equal(150, read.TabMinWidth);
                Assert.Equal(new[] { "Ctrl+Shift+F9" }, read.Hotkeys["view.fit"]);
                Assert.Equal(GestureCatalog.ZoomWheel, read.Gestures["wheel.windowed"]);
                Assert.Equal(new[] { "settings", "file.open" }, read.TitleBarButtons);

                // The badges compare with defaults, not with the loaded state.
                Assert.Equal(2, Card(window, "appearance-text-overlay").ChangedCount);
                Assert.Equal(1, Card(window, "appearance-theme").ChangedCount);
                Assert.Equal(1, Card(window, "tabs-strip-look").ChangedCount);
                Assert.Equal(1, Card(window, "mouse-shortcuts").ChangedCount);
                Assert.Equal(1, Card(window, "mouse-gestures").ChangedCount);
                Assert.Equal(1, Card(window, "window-title-bar").ChangedCount);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
