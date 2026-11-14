using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>
/// Headless contract of the Reset Glide card and its dialogs: placement, selection bar, confirmation flow against scratch
/// folders (never the real data), search, layout at the minimum width and PNG renders (set GLIDE_SAVE_RENDERS).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ResetCardUiTests : IDisposable
{
    public ResetCardUiTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

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

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Single(c => (c as StyledElement)?.Name == name);

    private static object? Call(SettingsWindow window, string name, params object?[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static SettingsWindow Open(double width, double height, bool expandReset)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
        window.Show();
        Call(window, "ShowCategory", "Windows", true, false, null!);
        window.Width = width;
        window.Height = height;
        Settle(window);
        var card = Card(window);
        card.IsExpanded = expandReset;
        Settle(window);
        // The Reset card is the last one of the page: scroll to it so window renders show it.
        Named<ScrollViewer>(window, "ContentScroll").ScrollToEnd();
        Settle(window);
        return window;
    }

    private static SettingsCard Card(SettingsWindow window) =>
        window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == "windows-reset");

    private static CheckBox Check(SettingsWindow window, ResetItemId id) => Field<Dictionary<ResetItemId, CheckBox>>(window, "_resetChecks")[id];

    private static string? SaveDirectory => Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    private static void Save(Visual visual, string name, double scale = 1.0)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        var size = new PixelSize((int)Math.Ceiling(visual.Bounds.Width * scale), (int)Math.Ceiling(visual.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    private static Rect BoundsIn(Visual visual, Visual root)
    {
        var m = visual.TransformToVisual(root) ?? throw new InvalidOperationException("Not in the same tree.");
        return new Rect(visual.Bounds.Size).TransformToAABB(m);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- placement and bar ----

    [Fact]
    public async Task Card_is_the_last_card_after_Diagnostics_collapsed_with_a_red_icon_and_never_a_changed_badge()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(930, 630, expandReset: false);
            try
            {
                var panel = Named<StackPanel>(window, "WindowsPanel");
                var cards = panel.Children.OfType<SettingsCard>().ToList();
                Assert.Equal("windows-reset", cards[^1].CardId);
                Assert.Equal("windows-diagnostics", cards[^2].CardId);
                var card = cards[^1];
                Assert.Equal("Reset Glide", card.Title);
                Assert.Equal("Choose what to wipe and start fresh", card.Subtitle);
                Assert.Equal("Reset", card.Icon);
                Assert.False(card.IsExpanded);
                Assert.Equal(Color.Parse("#E5645A"), ((ISolidColorBrush)card.IconBrush!).Color);

                // Tick everything: the reset items are actions, so no card badge and no pending settings change.
                Check(window, ResetItemId.AllSettings).IsChecked = true;
                Check(window, ResetItemId.Caches).IsChecked = true;
                Settle(window);
                Assert.Equal(0, card.ChangedCount);
                Assert.False(card.Badge.IsVisible);
                Assert.True(Enumerable.All(window.GetLogicalDescendants().OfType<SettingsCard>(), c => c.ChangedCount == 0));
                Assert.Equal("Up to date", Named<TextBlock>(window, "DirtyText").Text);
                Assert.False(Named<Button>(window, "ApplyButton").IsEnabled, "ticking reset items must not make Apply available");
                Save(window, "01-collapsed-windows-page.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selection_bar_counts_ticks_and_enables_the_red_button_only_when_something_is_ticked()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(930, 630, expandReset: true);
            try
            {
                var count = Named<TextBlock>(window, "ResetCountText");
                var go = Named<Button>(window, "ResetGoButton");
                Assert.Equal("Nothing selected", count.Text);
                Assert.Equal("Reset selected...", go.Content);
                Assert.False(go.IsEnabled);

                Check(window, ResetItemId.Caches).IsChecked = true;
                Check(window, ResetItemId.RegistryEntries).IsChecked = true;
                Assert.Equal("2 selected", count.Text);
                Assert.Equal("Reset 2 selected...", go.Content);
                Assert.True(go.IsEnabled);

                Click(Named<Button>(window, "ResetSelectAllButton"));
                Assert.Equal("10 selected", count.Text);
                Assert.Equal("Reset 10 selected...", go.Content);
                Assert.Equal(10, window.SelectedResetItems().Count);

                Click(Named<Button>(window, "ResetClearButton"));
                Assert.Equal("Nothing selected", count.Text);
                Assert.False(go.IsEnabled);
                Assert.Empty(window.SelectedResetItems());

                // Disabled at 45 % like the mockup, enabled fully opaque.
                Assert.Equal(0.45, go.Opacity, 3);
                Settle(window);
                Save(window, "02-expanded-nothing-selected.png");
                Click(Named<Button>(window, "ResetSelectAllButton"));
                Settle(window);
                Assert.Equal(1.0, go.Opacity, 3);
                Save(window, "03-expanded-all-selected.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- confirmation flow (scratch machine) ----

    private sealed class Flow : IDisposable
    {
        public ResetScratch Scratch { get; } = new();
        public FakeRegistry Registry { get; } = new();
        public FakeProcessHost Processes { get; } = new();
        public int ExitCalls { get; set; }
        public ResetConfirmWindow? Dialog { get; set; }
        public ResetResultWindow? Result { get; set; }
        public void Dispose() => Scratch.Dispose();

        public void Seed()
        {
            Scratch.Write(Scratch.Settings, "glide.settings.json", "{\"SettingsSchemaVersion\":7,\"ThemeChoice\":\"Light\"}");
            Scratch.Write(Scratch.Settings, "glide.history.json", "{\"Items\":[]}");
            Scratch.Write(Scratch.Settings, "thumbnail.settings.json", "{}");
            Scratch.Write(Scratch.Local, @"Startup Diagnostics\startup-timings.jsonl");
        }

        public SettingsWindow Wire(SettingsWindow window, IResetFileSystem? fs = null)
        {
            window.ResetServicesOverride = () => Scratch.Services(Registry, Processes, fs);
            window.ExitApplicationForReset = () => ExitCalls++;
            window.ResetDialogOpened += d => Dialog = d;
            window.ResetResultOpened += r => Result = r;
            return window;
        }
    }

    /// <summary>Starts the flow, lets <paramref name="act"/> drive the dialog once it is shown, and returns when the flow ended.</summary>
    private static async Task RunFlow(SettingsWindow window, Flow flow, Action<ResetConfirmWindow> act)
    {
        void OnOpened(ResetConfirmWindow dialog) => Dispatcher.UIThread.Post(() => act(dialog), DispatcherPriority.Background);
        window.ResetDialogOpened += OnOpened;
        try { await window.RunResetFlowAsync(); }
        finally { window.ResetDialogOpened -= OnOpened; }
        Settle(window);
    }

    [Fact]
    public async Task Dialog_lists_exactly_the_selected_items_and_the_registry_warning_only_with_registry_entries()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            var window = flow.Wire(Open(930, 630, expandReset: true));
            try
            {
                // 1) restart selection without registry
                Check(window, ResetItemId.RecentHistory).IsChecked = true;
                Check(window, ResetItemId.AllSettings).IsChecked = true;
                await RunFlow(window, flow, d =>
                {
                    Assert.Equal(["All settings", "Recent files and Home list"], d.ListedItems.ToArray());
                    Assert.Equal("Glide will close and restart. This can't be undone.", d.IntroText.Text);
                    Assert.True(d.BackupCheck.IsChecked);
                    Assert.False(d.WarningText.IsVisible);
                    Assert.Equal("Reset and restart", d.ConfirmButton.Content);
                    Assert.Equal(ResetConfirmWindow.DialogWidth, d.Width);
                    Assert.True(d.CancelButton.IsFocused, "the safe button has the focus");
                    Assert.False(d.ConfirmButton.IsDefault);
                    Save(d, "04-dialog-restart-items.png");
                    Click(d.CancelButton);
                });

                // 2) registry selected: warning visible; registry-only needs no restart, so the text changes too
                window.SelectedResetItems();
                Click(Named<Button>(window, "ResetClearButton"));
                Check(window, ResetItemId.RegistryEntries).IsChecked = true;
                await RunFlow(window, flow, d =>
                {
                    Assert.Equal(["Registry entries"], d.ListedItems.ToArray());
                    Assert.True(d.WarningText.IsVisible);
                    Assert.Equal(ResetConfirmWindow.RegistryWarningText, d.WarningText.Text);
                    Assert.Equal("This can't be undone.", d.IntroText.Text);
                    Assert.DoesNotContain("restart", d.IntroText.Text!);
                    Assert.Equal("Reset", d.ConfirmButton.Content);
                    Click(d.CancelButton);
                });

                // 3) registry together with a restart item: both the warning and the restart wording
                Check(window, ResetItemId.StartupEntries).IsChecked = true;
                await RunFlow(window, flow, d =>
                {
                    Assert.Equal(["Registry entries", "Startup and background entries"], d.ListedItems.ToArray());
                    Assert.True(d.WarningText.IsVisible);
                    Assert.Contains("restart", d.IntroText.Text!);
                    Save(d, "05-dialog-registry-warning.png");
                    Click(d.CancelButton);
                });

                // Nothing was done by any Cancel.
                Assert.Empty(flow.Processes.Started);
                Assert.Equal(0, flow.ExitCalls);
                Assert.Empty(flow.Registry.Writes);
                Assert.True(File.Exists(Path.Combine(flow.Scratch.Settings, "glide.settings.json")));
                Assert.False(Directory.Exists(flow.Scratch.Backups));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Cancel_and_Escape_change_nothing_and_Enter_does_not_confirm()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            var window = flow.Wire(Open(930, 630, expandReset: true));
            try
            {
                Check(window, ResetItemId.AllSettings).IsChecked = true;

                await RunFlow(window, flow, d => Click(d.CancelButton));
                await RunFlow(window, flow, d => d.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }));
                // Enter goes to the focused button, which is Cancel.
                await RunFlow(window, flow, d => d.CancelButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }) );
                await RunFlow(window, flow, d => d.Close(null));

                Assert.Empty(flow.Processes.Started);
                Assert.Equal(0, flow.ExitCalls);
                Assert.True(File.Exists(Path.Combine(flow.Scratch.Settings, "glide.settings.json")));
                Assert.False(Directory.Exists(flow.Scratch.Backups));
                // The selection survives a cancelled dialog.
                Assert.Equal([ResetItemId.AllSettings], window.SelectedResetItems().ToArray());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Confirming_a_restart_selection_saves_the_backup_starts_the_helper_and_exits_glide()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            var window = flow.Wire(Open(930, 630, expandReset: true));
            Check(window, ResetItemId.AllSettings).IsChecked = true;
            Check(window, ResetItemId.RecentHistory).IsChecked = true;
            await RunFlow(window, flow, d => Click(d.ConfirmButton));
            Dispatcher.UIThread.RunJobs();

            var backup = Assert.Single(Directory.GetDirectories(flow.Scratch.Backups));
            Assert.True(File.Exists(Path.Combine(backup, "glide.settings.json")));
            Assert.True(File.Exists(Path.Combine(backup, "glide.history.json")));
            Assert.True(File.Exists(Path.Combine(backup, "backup-info.txt")));
            var started = Assert.Single(flow.Processes.Started);
            Assert.True(ResetHelperArguments.TryParse(started.Args, out var parsed));
            Assert.Equal([ResetItemId.AllSettings, ResetItemId.RecentHistory], parsed!.Items.ToArray());
            Assert.Equal(1, flow.ExitCalls);
            Assert.False(window.IsVisible, "Settings closes before Glide exits");
            // The wipe is the helper's job after Glide has exited.
            Assert.True(File.Exists(Path.Combine(flow.Scratch.Settings, "glide.settings.json")));
        });
    }

    [Fact]
    public async Task Unticking_the_backup_skips_it()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            var window = flow.Wire(Open(930, 630, expandReset: true));
            Check(window, ResetItemId.LastSession).IsChecked = true;
            await RunFlow(window, flow, d => { d.BackupCheck.IsChecked = false; Click(d.ConfirmButton); });
            Assert.False(Directory.Exists(flow.Scratch.Backups));
            Assert.Single(flow.Processes.Started);
        });
    }

    [Fact]
    public async Task Confirming_caches_and_registry_runs_in_place_without_closing_glide_and_reports_the_result()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            flow.Registry.Set(ResetCatalog.ProgIdKey, "", "x");
            flow.Registry.Set(ResetCatalog.ThumbnailConfigKey, "Quality", 1);
            var window = flow.Wire(Open(930, 630, expandReset: true));
            try
            {
                Check(window, ResetItemId.Caches).IsChecked = true;
                Check(window, ResetItemId.RegistryEntries).IsChecked = true;
                await RunFlow(window, flow, d => Click(d.ConfirmButton));
                Dispatcher.UIThread.RunJobs();

                Assert.False(File.Exists(Path.Combine(flow.Scratch.Settings, "thumbnail.settings.json")));
                Assert.False(File.Exists(Path.Combine(flow.Scratch.Local, @"Startup Diagnostics\startup-timings.jsonl")));
                Assert.False(flow.Registry.KeyExists(ResetCatalog.ProgIdKey));
                Assert.False(flow.Registry.KeyExists(ResetCatalog.ThumbnailConfigKey));
                Assert.True(File.Exists(Path.Combine(flow.Scratch.Settings, "glide.settings.json")));
                Assert.Empty(flow.Processes.Started);
                Assert.Equal(0, flow.ExitCalls);
                Assert.True(window.IsVisible);
                var status = Named<TextBlock>(window, "ResetResultText");
                Assert.True(status.IsVisible);
                Assert.StartsWith("Done.", status.Text);
                Assert.Contains("Image and thumbnail caches", status.Text);
                Assert.Empty(window.SelectedResetItems());
                Assert.Contains("registry entries were removed", Named<TextBlock>(window, "WindowsIntegrationStatusText").Text);
                Assert.Null(flow.Result);
                Save(window, "06-expanded-after-in-place-reset.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task A_locked_file_shows_a_report_instead_of_pretending_it_worked()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            var window = flow.Wire(Open(930, 630, expandReset: true));
            using var held = new FileStream(Path.Combine(flow.Scratch.Settings, "thumbnail.settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Check(window, ResetItemId.Caches).IsChecked = true;
                window.ResetResultOpened += r => Dispatcher.UIThread.Post(() => { Save(r, "07-result-with-problems.png"); r.Close(); }, DispatcherPriority.Background);
                await RunFlow(window, flow, d => Click(d.ConfirmButton));

                Assert.NotNull(flow.Result);
                Assert.Contains("Image and thumbnail caches: not completed", flow.Result!.DetailsText.Text);
                Assert.Contains("thumbnail.settings.json", flow.Result.DetailsText.Text);
                Assert.Contains("in use", flow.Result.DetailsText.Text);
                Assert.Contains("could not be reset", Named<TextBlock>(window, "ResetResultText").Text);
                Assert.Equal([ResetItemId.Caches], window.SelectedResetItems().ToArray()); // still ticked: the user can retry
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task A_failed_helper_start_changes_nothing_and_does_not_exit_glide()
    {
        using var flow = new Flow();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            flow.Seed();
            flow.Processes.StartSucceeds = false;
            var window = flow.Wire(Open(930, 630, expandReset: true));
            try
            {
                Check(window, ResetItemId.AllSettings).IsChecked = true;
                window.ResetResultOpened += r => Dispatcher.UIThread.Post(r.Close, DispatcherPriority.Background);
                await RunFlow(window, flow, d => Click(d.ConfirmButton));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(0, flow.ExitCalls);
                Assert.True(window.IsVisible);
                Assert.NotNull(flow.Result);
                Assert.Contains("nothing was reset", Named<TextBlock>(window, "ResetResultText").Text);
                Assert.True(File.Exists(Path.Combine(flow.Scratch.Settings, "glide.settings.json")));
            }
            finally { window.Close(); }
        });
    }

    // ---- search ----

    [Theory]
    [InlineData("factory reset", "Reset Glide")]
    [InlineData("wipe", "Reset Glide")]
    [InlineData("restore defaults", "Reset Glide")]
    [InlineData("uninstall cleanup", "Remove Glide's registry entries")]
    [InlineData("regedit", "Remove Glide's registry entries")]
    [InlineData("recent", "Clear recent files and Home list")]
    [InlineData("cache", "Clear image and thumbnail caches")]
    [InlineData("ffmpeg", "Remove downloaded video codec pack")]
    [InlineData("start with windows", "Remove startup and background entries")]
    [InlineData("lost window", "Forget window sizes and positions")]
    public async Task Settings_search_finds_the_card_and_each_item(string query, string expectedLabel)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(930, 630, expandReset: false);
            try
            {
                var panel = Named<StackPanel>(window, "SearchPanel");
                Call(window, "RunSearchRebuild", query);
                var labels = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(expectedLabel, labels);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_search_result_tick_box_is_the_same_selection_as_the_card()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(930, 630, expandReset: false);
            try
            {
                Call(window, "RunSearchRebuild", "Clear image and thumbnail caches");
                var result = Named<StackPanel>(window, "SearchPanel").Children.OfType<Border>()
                    .First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Clear image and thumbnail caches"));
                var proxy = result.GetLogicalDescendants().OfType<CheckBox>().Single();
                Assert.False(Check(window, ResetItemId.Caches).IsChecked);
                // The result card toggles the real tick box (the small box on it is display only).
                Check(window, ResetItemId.Caches).IsChecked = true;
                Assert.Equal([ResetItemId.Caches], window.SelectedResetItems().ToArray());
                Assert.False(proxy.IsHitTestVisible);
                // ...and opening the result reveals the card.
                Call(window, "RevealSetting", "reset.caches");
                Settle(window);
                Assert.True(Card(window).IsExpanded);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- layout ----

    [Theory]
    [InlineData(720, 520)]
    [InlineData(930, 630)]
    [InlineData(1280, 800)]
    public async Task Expanded_card_fits_without_horizontal_overflow_and_bar_controls_are_aligned(double width, double height)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, height, expandReset: true);
            try
            {
                Check(window, ResetItemId.Caches).IsChecked = true;
                Settle(window);
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var card = Card(window);
                var cardBounds = BoundsIn(card, window);
                var viewport = BoundsIn(scroll, window);
                Assert.True(cardBounds.Left >= viewport.Left - 0.5 && cardBounds.Right <= viewport.Right + 0.5, $"card {cardBounds} vs viewport {viewport}");
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, "no horizontal scrolling");

                // Every tick box, its texts and every bar control stay inside the card body.
                var body = BoundsIn(card.Body, window);
                foreach (var check in Field<Dictionary<ResetItemId, CheckBox>>(window, "_resetChecks").Values)
                {
                    var bounds = BoundsIn(check, window);
                    Assert.True(bounds.Left >= body.Left - 0.5 && bounds.Right <= body.Right + 0.5, $"{check.Name} {bounds} outside {body}");
                    foreach (var text in check.GetVisualDescendants().OfType<TextBlock>())
                    {
                        var textBounds = BoundsIn(text, window);
                        Assert.True(textBounds.Right <= bounds.Right + 0.5, $"{text.Text} overflows its row at {width}");
                        Assert.True(textBounds.Bottom <= bounds.Bottom + 0.5, $"{text.Text} overflows the bottom of its row at {width}");
                        // Text that cannot wrap must fit on one line; wrapping text may take more lines but never gets cut.
                        var natural = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight };
                        natural.Measure(Size.Infinity);
                        Assert.True(text.TextWrapping == TextWrapping.Wrap || natural.DesiredSize.Width <= text.Bounds.Width + 0.5, $"{text.Text} is clipped at {width}");
                        Assert.True(text.TextWrapping != TextWrapping.Wrap || text.Bounds.Height + 0.5 >= natural.DesiredSize.Height, $"{text.Text} wraps into too little height at {width}");
                    }
                }

                var all = Named<Button>(window, "ResetSelectAllButton");
                var clear = Named<Button>(window, "ResetClearButton");
                var countText = Named<TextBlock>(window, "ResetCountText");
                var go = Named<Button>(window, "ResetGoButton");
                var rects = new[] { all, clear, go }.Select(b => BoundsIn(b, window)).ToArray();
                Assert.All(rects, r => Assert.Equal(30, r.Height, 0.5));
                // One shared centre line: both grey buttons, the count text and the red button.
                var centres = new[] { rects[0].Center.Y, rects[1].Center.Y, rects[2].Center.Y, BoundsIn(countText, window).Center.Y };
                Assert.True(centres.Max() - centres.Min() <= 0.75, "bar controls are not vertically aligned: " + string.Join(", ", centres));
                // Label centred inside each button.
                foreach (var button in new[] { all, clear, go })
                {
                    var content = button.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
                    var inner = BoundsIn(content, window);
                    var outer = BoundsIn(button, window);
                    Assert.True(Math.Abs(inner.Center.Y - outer.Center.Y) <= 1.0, $"{button.Name} label is off-centre vertically");
                    Assert.True(Math.Abs(inner.Center.X - outer.Center.X) <= 1.0, $"{button.Name} label is off-centre horizontally");
                }
                // The red button is right-aligned in the body, the grey ones start at its left edge.
                Assert.True(Math.Abs(rects[2].Right - body.Right) <= 1.0, $"red button right edge {rects[2].Right} vs body {body.Right}");
                Assert.True(Math.Abs(rects[0].Left - body.Left) <= 1.0);
                Assert.True(rects[1].Left > rects[0].Right && countText.Bounds.Width > 0);

                // Two equal columns.
                var first = BoundsIn(Check(window, ResetItemId.AllSettings), window);
                var second = BoundsIn(Check(window, ResetItemId.HotkeysAndGestures), window);
                Assert.Equal(first.Width, second.Width, 0.5);
                Assert.Equal(first.Top, second.Top, 0.5);
                Assert.True(second.Left >= first.Right - 0.5);

                Save(window, $"08-expanded-{(int)width}x{(int)height}.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Render_the_card_alone_at_the_mockup_width()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(930, 900, expandReset: true);
            try
            {
                var card = Card(window);
                Check(window, ResetItemId.Caches).IsChecked = true;
                Check(window, ResetItemId.RecentHistory).IsChecked = true;
                Settle(window);
                Save(card, "09-card-only-2-selected.png");
                card.IsExpanded = false;
                Settle(window);
                Save(card, "10-card-only-collapsed.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
