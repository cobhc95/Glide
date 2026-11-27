using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.7: closing and reopening Settings brings it back exactly as it was left (page, scroll, open sections), within a
/// session (the owner keeps the text in its settings state) and across restarts (it is saved with the settings). The search
/// box is cleared on purpose; opening at a setting still goes to that setting.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingsViewMemoryTests
{
    public SettingsViewMemoryTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>Runs on the UI thread with the app's theme: without it the page's ScrollViewer has no template, so no scroll extent.</summary>
    private static void Run(Action body) => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var app = Application.Current!;
        var styles = new Avalonia.Styling.IStyle[]
        {
            new Avalonia.Themes.Fluent.FluentTheme(),
            new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") }
        };
        for (var i = 0; i < styles.Length; i++) app.Styles.Insert(i, styles[i]);
        try { body(); }
        finally { foreach (var style in styles) app.Styles.Remove(style); }
    });

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => (c as StyledElement)?.Name == name);

    private static List<SettingsCard> CardsOf(SettingsWindow window, string panel) =>
        Named<StackPanel>(window, panel).GetLogicalDescendants().OfType<SettingsCard>().ToList();

    private static void ShowPage(SettingsWindow window, string key) =>
        typeof(SettingsWindow).GetMethod("ShowCategory", Flags)!.Invoke(window, [key, true, true, null!]);

    private static SettingsWindow Open(GlideSettingsState state)
    {
        var window = new SettingsWindow(state, (_, _) => { }) { Width = 930, Height = 700 };
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>Closes the window the way the app does and hands its captured view text to the state, like MainWindow.</summary>
    private static string CloseAndRemember(SettingsWindow window, GlideSettingsState state)
    {
        string? remembered = null;
        window.ViewMemoryChanged = text => remembered = text;
        window.Close();
        Settle(window);
        Assert.NotNull(remembered);
        state.SettingsViewMemory = remembered!;
        return remembered!;
    }

    // ------------------------------------------------------------------ the text format

    [Fact]
    public void Memory_text_round_trips_and_drops_unusable_entries()
    {
        var memory = new SettingsViewMemory("Interface",
            new Dictionary<string, double> { ["Interface"] = 412.54, ["Mouse"] = 80, ["Tabs"] = 0, ["Bad;Key"] = 5 },
            new Dictionary<string, bool> { ["tags-card"] = true, ["general-profiles"] = false, ["bad,id"] = true });
        var text = memory.Serialize();
        Assert.Equal("v1;page=Interface;scroll=Interface:412.5,Mouse:80;cards=-general-profiles,+tags-card", text);
        Assert.Equal(memory, SettingsViewMemory.Parse(text));
        Assert.Equal(text, SettingsViewMemory.Parse(text).Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v9;page=Interface")]
    [InlineData("v1;;=;page;scroll=:,x:y;cards=+,-,")]
    public void Unreadable_memory_parses_to_empty_without_throwing(string? text)
    {
        var memory = SettingsViewMemory.Parse(text);
        Assert.True(memory.IsEmpty);
        Assert.Equal(string.Empty, memory.Serialize());
    }

    [Fact]
    public void Memory_survives_the_settings_file_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-view-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = dir;
        try
        {
            var state = new GlideSettingsState { SettingsViewMemory = "v1;page=Tabs;scroll=Tabs:240;cards=+tabs-status-bar" };
            SettingsStore.Save(state);
            Assert.Equal(state.SettingsViewMemory, SettingsStore.Load().SettingsViewMemory);
            // Restore defaults is not allowed to throw the remembered page away, and a state clone carries it.
            Assert.Equal(state.SettingsViewMemory, SettingsDefaults.RestoreAllSettings(state).SettingsViewMemory);
            Assert.Equal(state.SettingsViewMemory, state.CloneState().SettingsViewMemory);
        }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public void Reopened_settings_returns_to_the_same_page_sections_and_scroll()
    {
        Run(() =>
        {
            var state = new GlideSettingsState();
            var first = Open(state);
            string expectedStates;
            double expectedOffset;
            try
            {
                ShowPage(first, "Interface");
                Settle(first);
                var cards = CardsOf(first, "InterfacePanel");
                Assert.True(cards.Count >= 3);
                foreach (var card in cards) card.IsExpanded = true;
                cards[0].IsExpanded = false;
                Settle(first);
                expectedStates = string.Join(",", cards.Select(c => c.IsExpanded ? "1" : "0"));

                var scroll = Named<ScrollViewer>(first, "ContentScroll");
                Assert.True(scroll.ScrollBarMaximum.Y > 120, "the page has to be taller than the window for this test");
                scroll.Offset = new Vector(0, 120);
                Settle(first);
                expectedOffset = scroll.Offset.Y;
                Assert.Equal(120, expectedOffset, 1);
            }
            finally { CloseAndRemember(first, state); }

            var second = Open(state);
            try
            {
                Assert.Equal("Interface", second.ActivePageKeyForTests);
                Assert.True(Named<StackPanel>(second, "InterfacePanel").IsVisible);
                Assert.False(Named<StackPanel>(second, "GeneralPanel").IsVisible);
                Assert.Equal(expectedStates, string.Join(",", CardsOf(second, "InterfacePanel").Select(c => c.IsExpanded ? "1" : "0")));
                Assert.Equal(expectedOffset, Named<ScrollViewer>(second, "ContentScroll").Offset.Y, 1);
                Assert.Equal("Window & navigation", Named<TextBlock>(second, "PageTitle").Text);
            }
            finally { second.Close(); }
        });
    }

    [Fact]
    public void Scroll_of_every_page_is_remembered_not_just_the_last_one()
    {
        Run(() =>
        {
            var state = new GlideSettingsState();
            var first = Open(state);
            try
            {
                foreach (var (page, panel, y) in new[] { ("Interface", "InterfacePanel", 90.0), ("Tabs", "TabsPanel", 60.0) })
                {
                    ShowPage(first, page);
                    Settle(first);
                    foreach (var card in CardsOf(first, panel)) card.IsExpanded = true;
                    Settle(first);
                    Named<ScrollViewer>(first, "ContentScroll").Offset = new Vector(0, y);
                    Settle(first);
                }
            }
            finally { CloseAndRemember(first, state); }

            var second = Open(state);
            try
            {
                Assert.Equal("Tabs", second.ActivePageKeyForTests);
                Assert.Equal(60, Named<ScrollViewer>(second, "ContentScroll").Offset.Y, 1);
                ShowPage(second, "Interface");
                Settle(second);
                Assert.Equal(90, Named<ScrollViewer>(second, "ContentScroll").Offset.Y, 1);
            }
            finally { second.Close(); }
        });
    }

    [Fact]
    public void Search_text_is_cleared_but_the_page_underneath_comes_back()
    {
        Run(() =>
        {
            var state = new GlideSettingsState();
            var first = Open(state);
            try
            {
                ShowPage(first, "Slideshow");
                Settle(first);
                Named<TextBox>(first, "SearchBox").Text = "zoom";
                Settle(first);
                first.FlushPendingSearch();
                Settle(first);
                Assert.True(Named<StackPanel>(first, "SearchPanel").IsVisible);
            }
            finally { CloseAndRemember(first, state); }

            var second = Open(state);
            try
            {
                Assert.Equal("Slideshow", second.ActivePageKeyForTests);
                Assert.True(string.IsNullOrEmpty(Named<TextBox>(second, "SearchBox").Text));
                Assert.False(Named<StackPanel>(second, "SearchPanel").IsVisible);
                Assert.True(Named<StackPanel>(second, "SlideshowPanel").IsVisible);
            }
            finally { second.Close(); }
        });
    }

    [Fact]
    public void Opening_at_a_setting_goes_to_that_setting_instead_of_the_remembered_page()
    {
        Run(() =>
        {
            var state = new GlideSettingsState { SettingsViewMemory = "v1;page=Mouse;scroll=Mouse:300" };
            var window = new SettingsWindow(state, (_, _) => { }) { Width = 930, Height = 700 };
            // What MainWindow.OpenSettingsAtSetting does: the command runs once the window has opened.
            window.Opened += (_, _) => window.RevealSettingFromCommand("tabs.switchToNewTabs");
            window.Show();
            try
            {
                Settle(window);
                Assert.Equal("Tabs", window.ActivePageKeyForTests);
                Assert.True(Named<StackPanel>(window, "TabsPanel").IsVisible);
                Assert.False(Named<StackPanel>(window, "MousePanel").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Remembered_page_or_sections_that_no_longer_exist_fall_back_to_the_default()
    {
        Run(() =>
        {
            foreach (var text in new[] { "v1;page=Retired;scroll=Retired:50;cards=-no-such-card", "v1;scroll=Retired:50;cards=+gone", "not a memory" })
            {
                var window = Open(new GlideSettingsState { SettingsViewMemory = text });
                try
                {
                    Assert.Equal("General", window.ActivePageKeyForTests);
                    Assert.True(Named<StackPanel>(window, "GeneralPanel").IsVisible);
                    Assert.Equal(0, Named<ScrollViewer>(window, "ContentScroll").Offset.Y, 1);
                }
                finally { window.Close(); }
            }
        });
    }

    [Fact]
    public void Always_expand_keeps_the_remembered_open_sections_for_when_it_is_switched_off()
    {
        Run(() =>
        {
            var state = new GlideSettingsState();
            var first = Open(state);
            string remembered;
            try
            {
                ShowPage(first, "Interface");
                Settle(first);
                var cards = CardsOf(first, "InterfacePanel");
                cards[0].IsExpanded = !cards[0].IsExpanded;
                Settle(first);
            }
            finally { remembered = CloseAndRemember(first, state); }
            Assert.Contains("cards=", remembered);

            // With Always expand on, every section is open; closing there must not turn that into "all sections open" memory.
            var alwaysOn = new GlideSettingsState { SettingsAlwaysExpand = true, SettingsViewMemory = remembered };
            var second = Open(alwaysOn);
            string afterAlwaysExpand;
            try { Assert.All(CardsOf(second, "InterfacePanel"), c => Assert.True(c.IsExpanded)); }
            finally { afterAlwaysExpand = CloseAndRemember(second, alwaysOn); }
            Assert.Equal(remembered.Split(';').First(x => x.StartsWith("cards=", StringComparison.Ordinal)),
                afterAlwaysExpand.Split(';').First(x => x.StartsWith("cards=", StringComparison.Ordinal)));
        });
    }
}
