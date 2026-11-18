using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Headless checks of the Settings mouse gesture panel and its search cards (no real UI driving).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Gestures")]
public sealed class GesturePanelTests
{
    public GesturePanelTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static object? Call(SettingsWindow window, string name, params object[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    [Fact]
    public async Task Panel_has_the_area_rail_grouped_rows_and_default_texts()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState();
            state.Gestures["tabs.middleClick"] = GestureCatalog.DuplicateTab;
            state.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Tabs, Input = GestureInputs.MiddleClick, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.DetachTab });
            var window = new SettingsWindow(state, (_, _) => { });
            window.Show();
            try
            {
                Call(window, "ShowCategory", "Mouse", true, false, null!);
                var host = window.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "GestureMatrixHost");
                var rail = host.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("gestureArea")).ToList();
                Assert.Equal(GestureCatalog.Areas.Select(a => a.Id), rail.Select(b => (string)b.Tag!));
                Assert.Contains("selected", rail[0].Classes);

                var editors = Field<Dictionary<string, GestureActionPicker>>(window, "_gestureEditors");
                Assert.Equal(GestureCatalog.Slots.Count, editors.Count);
                Assert.Equal("Default (previous / next)", editors["wheel.windowed"].DisplayText);
                Assert.DoesNotContain("changed", editors["wheel.windowed"].Classes);
                Assert.Equal("Duplicate tab", editors["tabs.middleClick"].DisplayText);
                Assert.Contains("changed", editors["tabs.middleClick"].Classes);

                // Windowed image table: CLICKS / DRAGS / WHEEL headers; OPTIONS and CUSTOM sit under the table.
                var pane = Field<StackPanel>(window, "_gesturePane");
                var extras = Field<StackPanel>(window, "_gestureExtrasPane");
                List<string?> Headers(Panel p) => p.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("gestureGroupHeader")).Select(t => t.Text).ToList();
                Assert.Equal(new[] { "CLICKS", "DRAGS", "WHEEL" }, Headers(pane));
                Assert.Equal(new[] { "OPTIONS", "CUSTOM" }, Headers(extras));

                // Tabs: no drags, no options; one custom row; changed badge = 1 row + 1 custom.
                Call(window, "SelectGestureArea", GestureAreas.Tabs);
                Assert.Equal(new[] { "CLICKS", "WHEEL" }, Headers(pane));
                Assert.Equal(new[] { "CUSTOM" }, Headers(extras));
                Assert.Single(extras.Children.OfType<Grid>(), g => g.Tag is string tag && tag.StartsWith("custom:", StringComparison.Ordinal));
                Assert.DoesNotContain(pane.Children.OfType<Grid>(), g => g.Tag is string tag && tag.StartsWith("custom:", StringComparison.Ordinal));
                var badges = Field<Dictionary<string, (Border Badge, TextBlock Text)>>(window, "_gestureBadges");
                Assert.True(badges[GestureAreas.Tabs].Badge.IsVisible);
                Assert.Equal("2", badges[GestureAreas.Tabs].Text.Text);
                Assert.False(badges[GestureAreas.Windowed].Badge.IsVisible);

                // Choosing an action updates the working map and the badge; Reset area clears it.
                Call(window, "OnGestureSlotChosen", "wheel.windowed", GestureCatalog.ZoomWheel);
                Assert.Equal(GestureCatalog.ZoomWheel, Field<Dictionary<string, string>>(window, "_workingGestures")["wheel.windowed"]);
                Assert.True(badges[GestureAreas.Windowed].Badge.IsVisible);
                Assert.Contains(GestureCatalog.ZoomWheel, Field<List<string>>(window, "_workingRecentGestureActions"));
                Call(window, "ResetGestureArea", GestureAreas.Windowed);
                Assert.False(badges[GestureAreas.Windowed].Badge.IsVisible);
                Assert.Equal("Default (previous / next)", editors["wheel.windowed"].DisplayText);

                // Secondary options were moved into their areas.
                Assert.False(window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "GestureSecondaryOptionsHost").IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Area_options_and_custom_block_sit_full_width_under_the_table_card()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState();
            state.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Windowed, Input = GestureInputs.MiddleClick, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.Fullscreen });
            var window = new SettingsWindow(state, (_, _) => { });
            window.Height = 1600;
            window.Show();
            try
            {
                Call(window, "OpenGestureArea", GestureAreas.Windowed, null!);
                window.UpdateLayout();
                var table = window.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "GestureMatrixHost");
                var pane = Field<StackPanel>(window, "_gesturePane");
                var extras = Field<StackPanel>(window, "_gestureExtrasPane");
                var invertWheel = window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "InvertWheelCheck");
                var custom = extras.Children.OfType<Grid>().Single(g => g.Tag is string tag && tag.StartsWith("custom:", StringComparison.Ordinal));
                var add = extras.Children.OfType<Button>().Single(b => b.Classes.Contains("gestureAddCustom"));

                // Not part of the table (neither its right pane nor the table card) ...
                foreach (Control c in new Control[] { extras, invertWheel, custom, add })
                {
                    Assert.False(pane.IsVisualAncestorOf(c) || ReferenceEquals(pane, c), $"{c} is inside the table's right pane.");
                    Assert.False(table.IsVisualAncestorOf(c), $"{c} is inside the table card.");
                }
                Assert.True(extras.IsVisualAncestorOf(invertWheel));
                Assert.True(extras.IsVisualAncestorOf(custom));

                // ... but inside the Mouse gestures card body, directly under the table card and above Reset all gestures.
                var gestureCard = Field<Dictionary<string, Glide.App.Controls.SettingsCard>>(window, "_cardById")["mouse-gestures"];
                Assert.True(gestureCard.IsVisualAncestorOf(extras));
                var body = (Panel)extras.Parent!;
                Assert.Same(table.Parent, body);
                Assert.Equal(body.Children.IndexOf(table) + 1, body.Children.IndexOf(extras));
                var resetAll = body.Children.OfType<Button>().Single(b => b.Content as string == "Reset all gestures");
                Assert.True(body.Children.IndexOf(extras) < body.Children.IndexOf(resetAll));
                var tableBottom = table.TranslatePoint(new Avalonia.Point(0, table.Bounds.Height), window)!.Value.Y;
                var extrasTop = extras.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.Y;
                Assert.True(extrasTop >= tableBottom, $"extras top {extrasTop} is above the table bottom {tableBottom}.");
                // Full width: as wide as the table card (give or take the card's 4 px left margin).
                Assert.True(Math.Abs(extras.Bounds.Width - table.Bounds.Width) <= 4.5, $"extras {extras.Bounds.Width} vs table {table.Bounds.Width}.");

                // Area-dependent: Selection shows its own options there; Tabs (no options) has no OPTIONS header.
                Call(window, "SelectGestureArea", GestureAreas.Selection);
                Assert.Contains(extras.Children, c => c is CheckBox { Name: "SelectionBufferCheck" });
                Assert.DoesNotContain(extras.Children, c => ReferenceEquals(c, invertWheel));
                Call(window, "SelectGestureArea", GestureAreas.Tabs);
                Assert.DoesNotContain(extras.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "OPTIONS");

                // Search "Open" of an option row switches to its area and finds it under the table.
                Call(window, "RevealSetting", "mouse.invertWheel");
                Assert.Equal(GestureAreas.Windowed, Field<string>(window, "_gestureSelectedArea"));
                Assert.True(extras.IsVisualAncestorOf(invertWheel));
                Assert.True(extras.IsVisible);

                // A gesture search inside the table hides the block; clearing it brings it back.
                var search = Field<TextBox>(window, "_gestureSearchBox");
                search.Text = "wheel";
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(extras.IsVisible, "the block should hide while the table shows gesture search results.");
                search.Text = string.Empty;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(extras.IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Changed_dot_and_class_only_on_rows_differing_from_default()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                Call(window, "ShowCategory", "Mouse", true, false, null!);
                var editors = Field<Dictionary<string, GestureActionPicker>>(window, "_gestureEditors");
                Assert.All(editors.Values, p =>
                {
                    Assert.DoesNotContain("changed", p.Classes);
                    Assert.False(p.ShowsChangedDot);
                });

                Call(window, "OnGestureSlotChosen", "wheel.windowed", GestureCatalog.ZoomWheel);
                Assert.Contains("changed", editors["wheel.windowed"].Classes);
                Assert.True(editors["wheel.windowed"].ShowsChangedDot);
                Assert.False(editors["tabs.middleClick"].ShowsChangedDot);

                Call(window, "ResetGestureArea", GestureAreas.Windowed);
                Assert.DoesNotContain("changed", editors["wheel.windowed"].Classes);
                Assert.False(editors["wheel.windowed"].ShowsChangedDot);

                Call(window, "OnGestureSlotChosen", "tabs.middleClick", GestureCatalog.DuplicateTab);
                Assert.True(editors["tabs.middleClick"].ShowsChangedDot);
                Call(window, "ResetGestureMatrixClicked", null!, null!);
                Assert.False(editors["tabs.middleClick"].ShowsChangedDot);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Global_search_finds_gesture_rows_and_custom_gestures_as_cards()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState();
            state.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Windowed, Input = GestureInputs.MiddleClick, Modifier = GestureModifiers.Alt, Action = GestureCatalog.Fullscreen });
            var window = new SettingsWindow(state, (_, _) => { });
            window.Show();
            try
            {
                var panel = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SearchPanel");
                bool Finds(string query, string label)
                {
                    Call(window, "RunSearchRebuild", query);
                    return panel.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == label);
                }
                Assert.True(Finds("scroll window", "Windowed image: Wheel (scroll)"));
                Assert.True(Finds("middle click tab", "Tabs: Middle click"));
                Assert.True(Finds("shift wheel", "Windowed image: Shift + wheel (scroll)"));
                Assert.True(Finds("title bar double", "Title bar: Double-left click"));
                Assert.True(Finds("custom alt", "Custom: Middle click + Alt"));
                Assert.True(Finds("scroll", "Reverse mouse-wheel direction"));
                // Cards carry the area pill, the breadcrumb and a live picker.
                Call(window, "RunSearchRebuild", "scroll window");
                Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Mouse and keyboard > Gestures > Windowed image > Wheel");
                Assert.Contains(panel.GetLogicalDescendants().OfType<GestureActionPicker>(), p => p.Id == "wheel.windowed" && p.DisplayText == "Default (previous / next)");
                // Open switches to the row's area.
                Call(window, "OpenGestureRow", "tabs.middleClick");
                Assert.Equal(GestureAreas.Tabs, Field<string>(window, "_gestureSelectedArea"));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
