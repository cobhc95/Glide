using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Tester round: status minimise button right-click (no bar menu), GIF controls visibility, text overlay line breaks,
/// Settings card anchoring, hotkey filter clear / Show all, Cancel's three choices, the footer change summary hit area and
/// trimmed customiser rows.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class TesterRoundFixesTests : IDisposable
{
    public TesterRoundFixesTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

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

    // ---- text overlay line breaks -----------------------------------------------------------------------------

    [Theory]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("a<BR/>b<br />c", "a\nb\nc")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"{index}\n{total}", "{index}\n{total}")]
    [InlineData("plain", "plain")]
    public void Text_overlay_template_supports_br_and_backslash_n(string template, string expected) =>
        Assert.Equal(expected, MainWindow.ApplyTextOverlayLineBreaks(template));

    [Fact]
    public void Text_overlay_template_help_documents_line_breaks()
    {
        var entry = SettingsCatalog.All.Single(s => s.Id == "overlay.pictureTemplate");
        Assert.Contains("<br>", entry.Description);
        Assert.Contains(@"\n", entry.Description);
    }

    // ---- main window -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Right_click_on_status_minimise_button_toggles_auto_hide_without_opening_the_bar_menu()
    {
        await OnMainWindow(window =>
        {
            var button = window.FindControl<Button>("StatusCollapseButton")!;
            Assert.True(window.IsStatusCollapseButtonSource(button));
            Assert.True(window.IsStatusCollapseButtonSource(button.GetVisualDescendants().OfType<Visual>().FirstOrDefault() ?? button));
            Assert.False(window.IsStatusCollapseButtonSource(window.FindControl<Control>("ViewerStatusSurface")));

            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
            if (center is not { } point || button.Bounds.Width <= 0) return;   // bar not laid out in this headless setup
            var before = Settings(window).StatusAutoHide;
            window.MouseDown(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False((bool)GetField(window, "_statusMenuOpen")!, "the status bar menu must not open over the minimise button");
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False((bool)GetField(window, "_statusMenuOpen")!);
            _ = before;
        });
    }

    [Fact]
    public async Task Gif_controls_follow_the_only_on_gifs_setting_on_every_settings_change()
    {
        await OnMainWindow(window =>
        {
            var group = window.FindControl<StackPanel>("GifGroup")!;
            Settings(window).StatusGifControlsOnlyOnGifs = false;
            Invoke(window, "ApplySettingsVisuals", false);
            Assert.True(group.IsVisible, "unchecked: GIF controls always show (greyed out without an animated GIF)");
            Assert.False(window.FindControl<Button>("GifPlayPauseButton")!.IsEnabled);

            Settings(window).StatusGifControlsOnlyOnGifs = true;
            Invoke(window, "ApplySettingsVisuals", false);
            Assert.False(group.IsVisible, "checked: hidden without an animated image");

            Settings(window).StatusGifControlsOnlyOnGifs = false;
            Invoke(window, "ApplySettingsVisuals", false);
            Assert.True(group.IsVisible);
        });
    }

    // ---- settings window ---------------------------------------------------------------------------------------

    [Fact]
    public void Collapsing_a_card_keeps_its_header_in_place_even_near_the_bottom()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = OpenSettings(new GlideSettingsState(), (_, _) => { });
            try
            {
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var pages = Field<Dictionary<string, List<SettingsCard>>>(window, "_cardsByPage");
                SettingsCard? card = null;
                // The largest page that, fully expanded and scrolled to the very bottom, still shows a card header (a page
                // whose last card is taller than the viewport shows none there).
                foreach (var (key, cards) in pages.OrderByDescending(p => p.Value.Count))
                {
                    Call(window, "ShowCategory", key, true, false, null!);
                    foreach (var c in cards) c.IsExpanded = true;
                    Settle(window);
                    if (scroll.ScrollBarMaximum.Y <= 0) continue; // the page must scroll for this test

                    // Scroll to the very bottom and collapse a card whose header is visible: the page shrinks below the offset.
                    scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
                    Settle(window);
                    card = cards.Where(c => c.IsEffectivelyVisible && c.TranslatePoint(new Point(0, 0), scroll) is { Y: >= 0 } p && p.Y < scroll.Viewport.Height)
                        .OrderBy(c => c.TranslatePoint(new Point(0, 0), scroll)!.Value.Y).FirstOrDefault();
                    if (card is not null) break;
                }
                Assert.NotNull(card);
                var before = card.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
                card.IsExpanded = false;
                var immediately = card.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
                Assert.Equal(before, immediately, 1);
                Settle(window);
                Assert.Equal(before, card.TranslatePoint(new Point(0, 0), scroll)!.Value.Y, 1);

                // Expanding again also keeps it, and scrolling to the top gives the slack back.
                card.IsExpanded = true;
                Settle(window);
                Assert.Equal(before, card.TranslatePoint(new Point(0, 0), scroll)!.Value.Y, 1);
                scroll.Offset = new Vector(0, 0);
                Settle(window);
                Assert.Equal(0, window.SettingsScrollSlackForTests, 1);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Hotkey_filter_clear_button_and_show_all_follow_the_filter()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = OpenSettings(new GlideSettingsState(), (_, _) => { });
            try
            {
                var box = Named<TextBox>(window, "HotkeyFilterBox");
                var showAll = Named<Button>(window, "ClearHotkeyLookupButton");
                var clear = window.FindControl<Button>("ClearHotkeyFilterButton")!;
                Assert.False(showAll.IsVisible);
                Assert.False(clear.IsVisible);
                box.Text = "zoom";
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.IsHotkeyListFiltered);
                Assert.True(showAll.IsVisible);
                Assert.True(clear.IsVisible);
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(string.IsNullOrEmpty(box.Text));
                Assert.False(window.IsHotkeyListFiltered);
                Assert.False(showAll.IsVisible);
                Assert.False(clear.IsVisible);

                box.Text = "slideshow";
                Dispatcher.UIThread.RunJobs();
                showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(string.IsNullOrEmpty(box.Text));
                Assert.False(showAll.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(SettingsWindow.UnsavedSettingsChoice.KeepEditing)]
    [InlineData(SettingsWindow.UnsavedSettingsChoice.Discard)]
    [InlineData(SettingsWindow.UnsavedSettingsChoice.Apply)]
    public void Cancel_with_unsaved_changes_offers_apply_discard_and_keep_editing(SettingsWindow.UnsavedSettingsChoice choice)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var commits = new List<GlideSettingsState>();
            var state = new GlideSettingsState { ConfirmDiscardSettingsChanges = true, PictureCounterFontSize = 13 };
            var window = OpenSettings(state, (s, commit) => { if (commit) commits.Add(s); });
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                var asked = 0;
                window.UnsavedChangesPromptForTests = _ => { asked++; return choice; };
                var size = Named<NumericUpDown>(window, "PictureFontSizeBox");
                size.Value = 21;
                Dispatcher.UIThread.RunJobs();
                Call(window, "CancelClicked", null, new RoutedEventArgs());
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, asked);
                switch (choice)
                {
                    case SettingsWindow.UnsavedSettingsChoice.KeepEditing:
                        Assert.False(closed);
                        Assert.Equal(21, (int)size.Value!);
                        Assert.Empty(commits);
                        break;
                    case SettingsWindow.UnsavedSettingsChoice.Discard:
                        Assert.True(closed);
                        Assert.Empty(commits);
                        break;
                    case SettingsWindow.UnsavedSettingsChoice.Apply:
                        Assert.True(closed);
                        Assert.Equal(21, Assert.Single(commits).PictureCounterFontSize);
                        break;
                }
            }
            finally { if (!closed) { window.UnsavedChangesPromptForTests = _ => SettingsWindow.UnsavedSettingsChoice.Discard; window.Close(); } }
        });
    }

    [Fact]
    public void Footer_change_summary_is_hit_testable_without_gaps()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = OpenSettings(new GlideSettingsState(), (_, _) => { });
            try
            {
                var host = Named<StackPanel>(window, "DirtySummaryHost");
                Assert.NotNull(host.Background);
                Assert.Equal(0, host.Spacing);
                // The change list is the summary's own popup, not a ToolTip (so two popups never show at once).
                Assert.Null(ToolTip.GetTip(host));
                foreach (var name in new[] { "DirtyText", "DirtyHintText" })
                {
                    var line = Named<TextBlock>(window, name);
                    Assert.False(line.IsHitTestVisible);
                    Assert.Null(ToolTip.GetTip(line));
                }
                var popup = window.FindControl<Popup>("DirtySummaryPopup")!;
                var popupText = window.FindControl<TextBlock>("DirtySummaryPopupText")!;
                Assert.False(popup.IsOpen);
                Assert.Equal("No net changes.", popupText.Text);

                // Click / Enter / Space toggle the popup (keyboard and touch path); it updates while open. Real pointer
                // hover and click are proven in Glide.Explorer.Tests HoverToolTipTests (this fixture does not hit-test).
                window.ToggleDirtySummaryPopup();
                Settle(window);
                Assert.True(popup.IsOpen, "toggle did not open the change list");
                var check = Named<CheckBox>(window, "OverlayHighlightCheck");
                check.IsChecked = !(check.IsChecked ?? false);
                Settle(window);
                Assert.Contains("→", popupText.Text);
                Assert.True(popup.IsOpen);
                host.Focus();
                host.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Settle(window);
                Assert.False(popup.IsOpen, "Enter did not close the change list");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Customizer_rows_trim_long_names_and_show_them_in_a_tooltip()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            EnsureAppStyles();
            var windows = new Window[] { new TitleBarCustomizerWindow(Array.Empty<string>()), new StatusBarCustomizerWindow(string.Empty) };
            foreach (var w in windows)
            {
                try
                {
                    var items = w.GetLogicalDescendants().OfType<ListBox>().SelectMany(l => l.Items.OfType<ListBoxItem>()).ToList();
                    Assert.NotEmpty(items);
                    Assert.All(items, i =>
                    {
                        // The tooltip is on the row itself (the element under the pointer); the trimmed label is not hit-testable.
                        Assert.True(i.IsEnabled && i.IsHitTestVisible);
                        var label = (i.Content as Control)?.GetLogicalDescendants().OfType<TextBlock>().Prepend(i.Content as TextBlock).OfType<TextBlock>()
                            .Last(t => !t.Classes.Contains("modernTitleSettingsGlyph"));
                        Assert.NotNull(label);
                        Assert.Equal(TextTrimming.CharacterEllipsis, label!.TextTrimming);
                        Assert.False(label.IsHitTestVisible);
                        Assert.Null(ToolTip.GetTip(label));
                        Assert.Equal(label.Text, ToolTip.GetTip(i) as string);
                    });
                    Assert.All(w.GetLogicalDescendants().OfType<ListBox>(), l =>
                        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(l)));
                }
                finally { w.Close(); }
            }
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static SettingsWindow OpenSettings(GlideSettingsState state, Action<GlideSettingsState, bool> apply)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(state, apply) { Width = 930, Height = 700 };
        window.Show();
        Settle(window);
        return window;
    }

    private static async Task OnMainWindow(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                SetField(window, "_settings", new GlideSettingsState { ShowStatusSurface = true });
                window.Width = 1000; window.Height = 700;
                window.Show();
                window.FindControl<Grid>("ImageView")!.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                Invoke(window, "ApplyStatusVisibility");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                body(window);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
            return Task.CompletedTask;
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => (c as StyledElement)?.Name == name);

    private static object? Call(SettingsWindow window, string name, params object?[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var m = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .First(x => x.Name == method && x.GetParameters().Length == args.Length);
        return m.Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    private static void SetField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
}
