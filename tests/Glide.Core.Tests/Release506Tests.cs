using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Settings;
using Xunit.Abstractions;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.6: text overlay live preview cost (font size, typing the template), the template help line, the text overlay
/// border option and the Mouse &amp; keyboard link to the Explorer scroll settings.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Regression")]
public sealed class Release506Tests
{
    private readonly ITestOutputHelper _output;
    public Release506Tests(AvaloniaHeadlessFixture fixture, ITestOutputHelper output) { _ = fixture; _output = output; }

    // ---- a/b: text overlay live preview ------------------------------------------------------------------------

    [Fact]
    public void Content_equality_compares_overlay_window_placements_by_value()
    {
        // The main window rebuilds this runtime-owned dictionary on every applied preview (KeepRuntimeOwnedValues);
        // comparing it by reference made every later preview look like an unrelated change.
        var a = new GlideSettingsState();
        a.OverlayWindowPlacements["k"] = new OverlayWindowPlacementState();
        var b = a.CloneState();
        b.OverlayWindowPlacements = a.OverlayWindowPlacements.ToDictionary(kv => kv.Key, kv => kv.Value with { }, StringComparer.OrdinalIgnoreCase);
        Assert.True(a.ContentEquals(b));
        b.OverlayWindowPlacements.Clear();
        Assert.False(a.ContentEquals(b));
    }

    [Fact]
    public async Task Every_font_size_preview_restyles_the_overlay_only()
    {
        await OnMainWindowWithSettings((main, settings, apply) =>
        {
            var size = Named<NumericUpDown>(settings, "PictureFontSizeBox");
            var visualsPasses = CountFullVisualPasses(main, () =>
            {
                for (var value = 14; value <= 20; value++)
                {
                    size.Value = value;
                    RunPreviewNow(settings);
                }
            });
            Assert.Equal(20, Live(main).PictureCounterFontSize);
            Assert.Equal(20, main.FindControl<TextBlock>("CornerCounterText")!.FontSize);
            Assert.Equal(0, visualsPasses);
        });
    }

    [Fact]
    public async Task Stage_C_owned_previews_are_routed_and_unknown_ones_fall_back_to_the_full_pass()
    {
        await OnMainWindowWithSettings((main, settings, apply) =>
        {
            // status.statZoom (StatusBar) and slideshow.interval (Slideshow, read at use) have narrow owner handlers.
            var routedBefore = main.SettingsRoutedPassesForTests;
            var routedPasses = CountFullVisualPasses(main, () =>
            {
                Named<CheckBox>(settings, "StatZoomCheck").IsChecked = !(Named<CheckBox>(settings, "StatZoomCheck").IsChecked == true);
                RunPreviewNow(settings);
                Named<NumericUpDown>(settings, "SlideshowIntervalBox").Value = 4500;
                RunPreviewNow(settings);
            });
            Assert.Equal(0, routedPasses);
            Assert.True(main.SettingsRoutedPassesForTests - routedBefore >= 2);
            Assert.Equal(4500, Live(main).SlideshowIntervalMs);

            // view.scrollbars has no narrow handler: the safe full re-apply runs.
            var fullPasses = CountFullVisualPasses(main, () =>
            {
                Named<CheckBox>(settings, "ShowScrollbarsCheck").IsChecked = !(Named<CheckBox>(settings, "ShowScrollbarsCheck").IsChecked == true);
                RunPreviewNow(settings);
            });
            Assert.Equal(1, fullPasses);
        });
    }

    [Fact]
    public async Task Typing_the_template_restyles_the_overlay_only_and_never_reads_the_controls_per_keystroke()
    {
        await OnMainWindowWithSettings((main, settings, apply) =>
        {
            var box = Named<TextBox>(settings, "PictureTemplateBox");
            var typed = "[{index}/{total}] {stem}";
            var reads = 0;
            var passes = CountFullVisualPasses(main, () =>
            {
                reads = CountReads(settings, () =>
                {
                    for (var i = 1; i <= typed.Length; i++)
                    {
                        box.Text = typed[..i];
                        Dispatcher.UIThread.RunJobs();
                    }
                });
                RunPreviewNow(settings);
            });
            Assert.Equal(0, reads);
            Assert.Equal(0, passes);
            Assert.Equal(typed, Live(main).PictureCounterTemplate);
        });
    }

    [Fact]
    public async Task Text_overlay_preview_cost_report()
    {
        await OnMainWindowWithSettings((main, settings, apply) =>
        {
            var box = Named<TextBox>(settings, "PictureTemplateBox");
            var size = Named<NumericUpDown>(settings, "PictureFontSizeBox");
            const int n = 30;
            double Time(Action a) { a(); var sw = Stopwatch.StartNew(); for (var i = 0; i < n; i++) a(); return sw.Elapsed.TotalMilliseconds / n; }
            var f = 0;
            var spinner = Time(() => { size.Value = 14 + (f++ % 10); RunPreviewNow(settings); main.UpdateLayout(); });
            var keystroke = Time(() => { box.Text = "[{index}/{total}]" + new string('x', f++ % 10); RunPreviewNow(settings); main.UpdateLayout(); });
            _output.WriteLine($"font size change, preview + layout: {spinner:0.00} ms");
            _output.WriteLine($"template keystroke, preview + layout: {keystroke:0.00} ms");
        });
    }

    // ---- c: help text ------------------------------------------------------------------------------------------

    [Fact]
    public void Text_overlay_help_shows_a_literal_backslash_n_everywhere()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                var box = Named<TextBox>(window, "PictureTemplateBox");
                var note = ((Panel)box.GetVisualParent()!.GetVisualParent()!).Children
                    .OfType<TextBlock>().Single(t => t.Classes.Contains("phaseNote") && t.Text!.StartsWith("Tokens:", StringComparison.Ordinal));
                Assert.Contains(@"Line break: <br> or \n", note.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", note.Text!.Replace(@"\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
                var tip = Assert.IsType<string>(ToolTip.GetTip(box));
                Assert.Contains(@"<br> or \n", tip, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", tip.Replace(@"\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

                var entry = SettingsCatalog.All.Single(s => s.Id == "overlay.pictureTemplate");
                Assert.Contains(@"<br> or \n", entry.Description, StringComparison.Ordinal);
                // The search result card shows the catalogue description as written.
                Call(window, "RunSearchRebuild", "Text overlay template");
                var texts = Named<StackPanel>(window, "SearchPanel").GetLogicalDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? [])).ToList();
                Assert.Contains(texts, t => t.Contains(@"<br> or \n", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
    }

    // ---- d: border option -------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_overlay_border_is_a_searchable_setting_that_boxes_every_line()
    {
        var entry = SettingsCatalog.All.SingleOrDefault(s => s.Id == "overlay.pictureBorder");
        Assert.NotNull(entry);
        Assert.Equal(SettingKind.Toggle, entry!.Kind);
        Assert.Equal(false, entry.DefaultValue);
        Assert.Equal("Text overlay border", entry.Label);
        Assert.NotNull(SettingAccessors.Find("overlay.pictureBorder"));
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == "overlay.pictureBorder");

        await OnMainWindowWithSettings((main, settings, apply) =>
        {
            var check = Named<CheckBox>(settings, "PictureBorderCheck");
            Assert.False(check.IsChecked);
            var host = main.FindControl<Border>("CornerCounterHost")!;
            Assert.Equal(default, host.BorderThickness);
            Assert.Null(host.Background);

            var passes = CountFullVisualPasses(main, () => { check.IsChecked = true; RunPreviewNow(settings); });
            Assert.Equal(0, passes);
            Assert.True(host.BorderThickness.Top > 0);
            Assert.NotNull(host.Background);
            Assert.True(host.CornerRadius.TopLeft > 0);
            // One box around every line: the counter and the folder position line are both inside it.
            Assert.Same(host, main.FindControl<TextBlock>("FolderCounterText")!.FindAncestorOfType<Border>(true)!
                .GetSelfAndVisualAncestors().OfType<Border>().First(b => b.Name == "CornerCounterHost"));

            check.IsChecked = false;
            RunPreviewNow(settings);
            Assert.Equal(default, host.BorderThickness);
            Assert.Null(host.Background);

            Call(settings, "RunSearchRebuild", "border");
            Assert.Contains(Named<StackPanel>(settings, "SearchPanel").GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Text overlay border");
        });
    }

    // ---- 8: Explorer scroll settings from Mouse & keyboard -----------------------------------------------------

    [Fact]
    public void Mouse_page_links_to_the_explorer_scroll_settings_and_reveals_them()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() => WithAppStyles(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                var mouse = Named<StackPanel>(window, "MousePanel");
                var link = mouse.GetLogicalDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "ExplorerScrollLinkButton");
                Assert.NotNull(link);
                Assert.Contains(link!.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Explorer scroll speed and amount →");
                // A link, not a copy: the only scroll editors are the ones on the Explorer tab card.
                Assert.DoesNotContain(mouse.GetLogicalDescendants().OfType<NumericUpDown>(), n => n.Name?.StartsWith("ExplorerScroll", StringComparison.Ordinal) == true);

                Call(window, "ShowCategory", "Mouse", true, false, null!);
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

                Assert.True(Named<StackPanel>(window, "InterfacePanel").IsVisible);
                Assert.False(mouse.IsVisible);
                var card = window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == "window-explorer");
                Assert.True(card.IsExpanded);
                var target = Named<Control>(window, "ExplorerScrollSettingsGroup");
                Assert.NotNull(AdornerLayer.GetAdorner(target));
                foreach (var name in new[] { "ExplorerScrollModeCombo", "ExplorerScrollRowsBox", "ExplorerScrollPixelsBox", "ExplorerScrollSpeedBox" })
                    Assert.True(Named<Control>(window, name).GetVisualAncestors().Contains(target), $"{name} is not inside the highlighted group");
                var viewport = Named<ScrollViewer>(window, "ContentScroll");
                var top = target.TranslatePoint(new Point(0, 0), viewport)!.Value.Y;
                Assert.True(top >= 0 && top < viewport.Viewport.Height, $"the scroll settings are not in view (top {top}, viewport {viewport.Viewport}, offset {viewport.Offset}, extent {viewport.Extent})");
            }
            finally { window.Close(); }
        }));
    }

    [Fact]
    public void Explorer_tab_card_links_to_the_new_tab_settings_and_reveals_them()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() => WithAppStyles(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                var explorerCard = window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == "window-explorer");
                var link = explorerCard.GetLogicalDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "NewTabsLinkButton");
                Assert.NotNull(link);
                var texts = link!.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Opening pictures in new tabs →", texts);
                Assert.Contains("Foreground or background, position, and the key to open the other way. Opens Tabs & status bar > Tabs.", texts);
                // A link, not a copy: the new-tab editors exist once, on the Tabs card.
                Assert.DoesNotContain(explorerCard.GetLogicalDescendants().OfType<Control>(),
                    c => c.Name is "NewTabPositionCombo" or "SwitchToNewTabsCombo" or "NewTabPositionKeyCombo" or "NewTabForegroundKeyCombo");

                Call(window, "ShowCategory", "Interface", true, false, null!);
                Settle(window);
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);

                Assert.True(Named<StackPanel>(window, "TabsPanel").IsVisible);
                Assert.False(Named<StackPanel>(window, "InterfacePanel").IsVisible);
                var card = window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == "tabs-tabs");
                Assert.True(card.IsExpanded);
                var target = Named<Control>(window, "NewTabSettingsGroup");
                Assert.NotNull(AdornerLayer.GetAdorner(target));
                foreach (var name in new[] { "NewTabPositionCombo", "NewTabPositionKeyCombo", "SwitchToNewTabsCombo", "NewTabForegroundKeyCombo" })
                    Assert.True(Named<Control>(window, name).GetVisualAncestors().Contains(target), $"{name} is not inside the highlighted group");
                var viewport = Named<ScrollViewer>(window, "ContentScroll");
                var top = target.TranslatePoint(new Point(0, 0), viewport)!.Value.Y;
                Assert.True(top >= 0 && top < viewport.Viewport.Height, $"the new-tab settings are not in view (top {top}, viewport {viewport.Viewport}, offset {viewport.Offset}, extent {viewport.Extent})");
            }
            finally { window.Close(); }
        }));
    }

    /// <summary>
    /// Hands-on report (5.0.6): search "text overlay", click Open on "Text overlay template": the page switched to
    /// Appearance &amp; viewing but stayed at the top, nothing highlighted in view (cards expanded with "Expand all").
    /// The Mouse &amp; keyboard link to the Explorer scroll settings did scroll. Both entry points are driven through
    /// their real buttons, with the cards collapsed and expanded, and must land on their row.
    /// </summary>
    [Theory]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("link", false)]
    [InlineData("link", true)]
    public void Search_Open_and_the_scroll_link_scroll_to_the_setting_and_highlight_it(string entry, bool expandAll)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() => WithAppStyles(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                var (page, targetName) = entry == "search" ? ("Appearance", "PictureTemplateBox") : ("Interface", "ExplorerScrollSettingsGroup");
                Settle(window);
                // "Expand all" on the target page (or leave every card collapsed), then go back to where the user starts.
                Call(window, "ShowCategory", page, true, false, null!);
                Settle(window);
                if (expandAll) Call(window, "ExpandAllCardsClicked", null!, new RoutedEventArgs());
                else Call(window, "CollapseAllCardsClicked", null!, new RoutedEventArgs());
                Settle(window);
                Call(window, "ShowCategory", entry == "search" ? "General" : "Mouse", true, false, null!);
                Settle(window);

                Button button;
                if (entry == "search")
                {
                    Named<TextBox>(window, "SearchBox").Text = "text overlay";
                    Settle(window);
                    window.FlushPendingSearch();
                    Settle(window);
                    var result = Named<StackPanel>(window, "SearchPanel").Children.OfType<Border>()
                        .First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => TextOf(t) == "Text overlay template"));
                    button = result.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Open");
                }
                else button = Named<Button>(window, "ExplorerScrollLinkButton");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);

                Assert.True(Named<StackPanel>(window, page + "Panel").IsVisible);
                var editor = Named<Control>(window, targetName);
                var card = window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.GetLogicalDescendants().Contains(editor));
                Assert.True(card.IsExpanded);
                var row = card.Body.Children.OfType<Control>().Single(c => c == editor || c.GetLogicalDescendants().Contains(editor));
                Assert.NotNull(AdornerLayer.GetAdorner(row));
                var viewport = Named<ScrollViewer>(window, "ContentScroll");
                var top = row.TranslatePoint(new Point(0, 0), viewport)!.Value.Y;
                // The reveal puts the row about 72 px below the top of the page viewport (less only at the very top of a page;
                // lower only when the page is scrolled to its end, as for the last card on a page, and then the whole row shows).
                var atPageEnd = viewport.Offset.Y >= viewport.ScrollBarMaximum.Y - 0.5;
                var fits = atPageEnd ? top + row.Bounds.Height <= viewport.Viewport.Height : top <= 160;
                Assert.True(top >= 0 && fits && top < viewport.Viewport.Height,
                    $"{entry}: the row is not revealed (top {top}, viewport {viewport.Viewport}, offset {viewport.Offset}, extent {viewport.Extent})");
            }
            finally { window.Close(); }
        }));
    }

    private static string TextOf(TextBlock t) =>
        t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    /// <summary>
    /// A message loop in its worst order: all queued dispatcher work (Loaded-priority posts included) runs, then a layout
    /// pass. A posted reveal therefore runs before the page it just made visible has been laid out, unless it lays the
    /// page out itself. (The headless platform lays out only when asked.)
    /// </summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    [Theory]
    [InlineData("mouse wheel")]
    [InlineData("scroll")]
    [InlineData("wheel speed")]
    public void Settings_search_finds_the_explorer_scroll_settings(string query)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            window.Show();
            try
            {
                Call(window, "RunSearchRebuild", query);
                var labels = Named<StackPanel>(window, "SearchPanel").GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();
                Assert.Contains("Explorer scroll speed", labels);
                Assert.Contains("Explorer scroll amount", labels);
            }
            finally { window.Close(); }
        });
    }

    // ---- Explorer default view labels -------------------------------------------------------------------------

    [Fact]
    public void Explorer_default_view_shows_the_explorer_tab_labels_and_keeps_the_stored_names()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var commits = new List<GlideSettingsState>();
            var window = new SettingsWindow(new GlideSettingsState { ExplorerDefaultView = "Small thumbnails" }, (s, commit) => { if (commit) commits.Add(s); });
            window.Show();
            try
            {
                var combo = Named<ComboBox>(window, "ExplorerDefaultViewCombo");
                Assert.Equal(Glide.App.Explorer.ExplorerViewModes.Labels, combo.Items.OfType<ComboBoxItem>().Select(i => i.Content as string));
                Assert.Equal("Thumbnails (S)", (combo.SelectedItem as ComboBoxItem)?.Content);
                combo.SelectedIndex = 0;
                var read = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                Assert.Equal("Extra large thumbnails", read.ExplorerDefaultView);
                Call(window, "RunSearchRebuild", "Thumbnails (XL)");
                Assert.Contains(Named<StackPanel>(window, "SearchPanel").GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Explorer default view");
            }
            finally { window.Close(); }
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>The real theme (templates, so scroll viewers lay out) for the body only.</summary>
    private static void WithAppStyles(Action body)
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
    }

    /// <summary>Runs the pending live preview at once instead of waiting for the debounce timer.</summary>
    private static void RunPreviewNow(SettingsWindow settings)
    {
        Dispatcher.UIThread.RunJobs();
        settings.FlushPendingLivePreviewForTests();
        Dispatcher.UIThread.RunJobs();
    }

    private static int CountFullVisualPasses(MainWindow main, Action body)
    {
        var before = main.SettingsVisualsPassesForTests;
        body();
        return main.SettingsVisualsPassesForTests - before;
    }

    private static int CountReads(SettingsWindow settings, Action body)
    {
        var before = settings.ReadControlsCountForTests;
        body();
        return settings.ReadControlsCountForTests - before;
    }

    private static GlideSettingsState Live(MainWindow main) => (GlideSettingsState)GetField(main, "_settings")!;

    private static async Task OnMainWindowWithSettings(Action<MainWindow, SettingsWindow, Action<GlideSettingsState, bool>> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var main = new MainWindow();
            SettingsWindow? settings = null;
            try
            {
                SetField(main, "_settings", new GlideSettingsState { ShowStatusSurface = true });
                main.Width = 1000; main.Height = 700;
                main.Show();
                main.FindControl<Grid>("ImageView")!.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                main.UpdateLayout();
                var applyMethod = typeof(MainWindow).GetMethod("ApplySettingsFromDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var apply = (Action<GlideSettingsState, bool>)Delegate.CreateDelegate(typeof(Action<GlideSettingsState, bool>), main, applyMethod);
                settings = new SettingsWindow(Live(main), apply) { Width = 930, Height = 700 };
                settings.Show();
                Dispatcher.UIThread.RunJobs();
                settings.UpdateLayout();
                // As in the app: the first preview after opening may carry normalisation; the ones that follow are measured.
                Named<CheckBox>(settings, "PictureBoldCheck").IsChecked = !Named<CheckBox>(settings, "PictureBoldCheck").IsChecked;
                RunPreviewNow(settings);
                body(main, settings, apply);
            }
            finally
            {
                settings?.Close();
                main.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
            return Task.CompletedTask;
        });
        TempDir.TryDelete(root);
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => (c as StyledElement)?.Name == name);

    private static object? Call(SettingsWindow window, string name, params object?[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
}
