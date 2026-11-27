using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>Vertigo round 4, B1-B3: Alt+click / Alt+drag on the text overlay.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Overlay")]
public sealed class TextOverlayAltDragTests : IDisposable
{
    public TextOverlayAltDragTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly List<Avalonia.Styling.IStyle> InstalledStyles = new();

    // Without the theme the window has no template, so nothing is rendered or hit-tested and pointer input goes nowhere.
    private static void EnsureAppStyles()
    {
        if (InstalledStyles.Count > 0) return;
        var app = Application.Current!;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        InstalledStyles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        InstalledStyles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
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

    [Theory]
    [InlineData("Top right", 0, 0)]
    [InlineData("Top right", 3, 2)]
    [InlineData("Top left", 40, 30)]
    [InlineData("Bottom right", -20, -10)]
    [InlineData("Top center", 2000, 0)]
    [InlineData("Title bar \u2013 left", 0, 0)]
    [InlineData("Title bar \u2013 right", 4, 4)]
    public async Task Alt_click_or_drag_keeps_the_overlay_inside_the_picture_area(string placement, int dx, int dy)
    {
        await OnMainWindow(window =>
        {
            var s = Settings(window);
            s.PictureCounterPositionTopTabs = placement;
            var host = window.FindControl<Border>("CornerCounterHost")!;
            var image = window.FindControl<Grid>("ImageView")!;
            window.FindControl<TextBlock>("CornerCounterText")!.Text = "[12/345]  sample.jpg";
            host.IsVisible = true;
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            var p = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
            var pointer = NewPointer();
            Press(window, pointer, p, KeyModifiers.Alt);
            Move(window, pointer, p + new Point(dx, dy), KeyModifiers.Alt);
            Release(window, pointer, p + new Point(dx, dy), KeyModifiers.Alt);
            Settle(window);
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            var topLeft = host.TranslatePoint(new Point(0, 0), image)!.Value;
            Assert.True(ReferenceEquals(host.Parent, image) || dx == 0 && dy == 0, $"a drag drops the overlay over the picture: {s.PictureCounterPositionTopTabs} {s.PictureCounterOffsets} p={p} tl={host.TranslatePoint(default, image)} img={image.Bounds} host={host.Bounds}");
            if (!ReferenceEquals(host.Parent, image)) return;
            Assert.InRange(topLeft.X, -1, image.Bounds.Width - host.Bounds.Width + 1);
            Assert.InRange(topLeft.Y, -1, image.Bounds.Height - host.Bounds.Height + 1);
            if (dx == 0 && dy == 0)
                Assert.InRange(Math.Abs(topLeft.X - (p.X - host.Bounds.Width / 2 - image.TranslatePoint(default, window)!.Value.X)), 0, 2);
        });
    }

    [Theory]
    [InlineData("Top left", 3500, 0)]
    [InlineData("Top right", 900, 900)]
    [InlineData("Bottom center", -3000, -3000)]
    public async Task A_saved_offset_never_draws_the_overlay_off_screen(string anchor, int x, int y)
    {
        await OnMainWindow(window =>
        {
            var s = Settings(window);
            TextOverlayPlacements.SetCustom(s, false, anchor, x, y);
            s.PictureCounterPositionTopTabs = TextOverlayPlacements.Custom;
            var host = window.FindControl<Border>("CornerCounterHost")!;
            var image = window.FindControl<Grid>("ImageView")!;
            window.FindControl<TextBlock>("CornerCounterText")!.Text = "[12/345]  sample.jpg";
            host.IsVisible = true;
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            var b = host.Bounds.TransformToAABB(host.RenderTransform?.Value ?? Matrix.Identity);
            var tl = host.TranslatePoint(new Point(0, 0), image)!.Value;
            Assert.InRange(tl.X, -1, image.Bounds.Width - host.Bounds.Width + 1);
            Assert.InRange(tl.Y, -1, image.Bounds.Height - host.Bounds.Height + 1);
            _ = b;
        });
    }

    [Fact]
    public async Task Title_bar_overlay_is_one_line_with_its_own_look_and_coloured_separators()
    {
        await OnMainWindow(window =>
        {
            var s = Settings(window);
            s.PictureCounterPositionTopTabs = TextOverlayPlacements.TitleBarLeft;
            s.PictureCounterTemplate = "[{index}/{total}]<br>{total}";
            s.TextOverlayTitleBarFontSize = 21;
            s.TextOverlaySeparatorColor = "Amber";
            window.FindControl<Border>("CornerCounterHost")!.IsVisible = true;
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            var text = window.FindControl<TextBlock>("CornerCounterText")!;
            var shadow = window.FindControl<TextBlock>("CornerCounterShadowText")!;
            Assert.DoesNotContain("\n", shadow.Text);
            Assert.Contains("\u2022", shadow.Text);   // the line break became a separator
            Assert.Equal(21, text.FontSize);
            Assert.Contains(text.Inlines!.OfType<Avalonia.Controls.Documents.Run>(), r => r.Text!.Contains('\u2022') && r.Foreground is Avalonia.Media.ISolidColorBrush b && b.Color == Avalonia.Media.Color.Parse("#F1B84B"));

            // Over the picture: the picture look and its line break.
            s.PictureCounterPositionTopTabs = "Top right";
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            Assert.Contains("\n", shadow.Text);
            Assert.Equal(s.PictureCounterFontSize, text.FontSize);
        });
    }

    [Fact]
    public async Task Settings_offset_boxes_are_usable_and_sit_in_the_placement_card_in_use()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            EnsureAppStyles();
            foreach (var side in new[] { false, true })
            {
                var state = new GlideSettingsState { TabsEnabled = true, TabStripPosition = side ? "Side" : "Top" };
                var window = new SettingsWindow(state, (_, _) => { }) { Width = 930, Height = 700 };
                window.Show();
                try
                {
                    Settle(window);
                    var box = window.FindControl<NumericUpDown>("PictureOffsetXBox")!;
                    var card = window.FindControl<Border>(side ? "SideTabsPlacementCard" : "TopTabsPlacementCard")!;
                    Assert.Contains(card, Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(box));
                    // B7: the tokens are laid out in columns.
                    var grid = window.FindControl<Grid>("PictureTemplateTokenGrid")!;
                    Assert.True(grid.ColumnDefinitions.Count >= 4);
                    Assert.Contains(grid.Children.OfType<TextBlock>(), t => t.Text == "{MP}");
                    Assert.DoesNotContain(grid.Children.OfType<TextBlock>(), t => t.Text == "{megapixels}");
                }
                finally { window.Close(); }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Settings_separator_rows_have_visible_labels_beside_their_combos_and_tokens_are_listed_once()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            EnsureAppStyles();
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 1000 };
            window.Show();
            try
            {
                window.GetType().GetMethod("ShowCategory", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object?[] { "Appearance", true, false, null });
                Settle(window);
                var card = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Glide.App.Controls.SettingsCard>().First(c => c.CardId == "appearance-text-overlay");
                card.IsExpanded = true;
                Settle(window);
                foreach (var (comboName, label) in new[] { ("PictureSeparatorCombo", "Separator between parts ({sep})"), ("SeparatorColorCombo", "Separator colour") })
                {
                    var combo = window.FindControl<ComboBox>(comboName)!;
                    Assert.True(string.IsNullOrEmpty(combo.PlaceholderText), "no watermark text inside the combo");
                    var text = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>().First(t => t.Text == label);
                    Assert.True(text.IsEffectivelyVisible && text.Bounds.Width > 0);
                    var tl = text.TranslatePoint(default, window)!.Value; var cl = combo.TranslatePoint(default, window)!.Value;
                    var textRect = new Rect(tl, text.Bounds.Size); var comboRect = new Rect(cl, combo.Bounds.Size);
                    Assert.False(textRect.Intersects(comboRect), $"{label} overlaps its combo");
                    Assert.True(tl.X < cl.X, "label sits to the left of the combo");
                }
                var grid = window.FindControl<Grid>("PictureTemplateTokenGrid")!;
                var tokens = grid.Children.OfType<TextBlock>().Where(t => t.Text!.StartsWith('{')).Select(t => t.Text!).ToList();
                Assert.Equal(tokens.Count, tokens.Distinct().Count());
                var meanings = grid.Children.OfType<TextBlock>().Where(t => !t.Text!.StartsWith('{')).Select(t => t.Text!).ToList();
                Assert.Equal(meanings.Count, meanings.Distinct().Count());
                Assert.Single(meanings, m => m.Contains("extension", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(tokens, t => t is "{extension}" or "{filename}");
                var dir = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    window.FindControl<ComboBox>("SeparatorColorCombo")!.BringIntoView(); Settle(window);
                    var scroll = Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(grid).OfType<ScrollViewer>().FirstOrDefault();
                    if (scroll is not null) { scroll.Offset = new Vector(0, scroll.Offset.Y + 150); Settle(window); }
                    var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(dir, "separator-settings.png"));
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Overlay_has_an_alt_drag_hint_of_its_own_and_no_tooltip_for_the_tooltip_service_to_close()
    {
        await OnMainWindow(window =>
        {
            var host = window.FindControl<Border>("CornerCounterHost")!;
            Assert.Contains("Alt+drag to move", MainWindow.TextOverlayToolTip, StringComparison.Ordinal);
            Assert.Null(ToolTip.GetTip(host));   // 5.2.0: the hint is a label of the window, not an Avalonia tooltip (the tooltip service closed it on the desktop)
        });
    }

    [Fact]
    public async Task Holding_alt_over_the_overlay_freezes_status_bar_auto_hide()
    {
        await OnMainWindow(window =>
        {
            var host = window.FindControl<Border>("CornerCounterHost")!;
            window.FindControl<TextBlock>("CornerCounterText")!.Text = "[1/2]";
            host.IsVisible = true;
            Invoke(window, "ApplyPictureCounterVisuals");
            Settle(window);
            var p = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
            var pointer = NewPointer();
            Press(window, pointer, p, KeyModifiers.Alt);
            Assert.NotNull(GetField(window, "_textOverlayDragPointer"));
            Assert.True((bool)window.GetType().GetProperty("StatusAutoHideFrozen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            Release(window, pointer, p, KeyModifiers.None);
            Settle(window);
            Assert.False((bool)window.GetType().GetProperty("StatusAutoHideFrozen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
        });
    }

    private static IPointer NewPointer() => new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);

    // Pointer events raised on the overlay text: they travel the real routing (the window's tunnel handlers).
    private static void Press(MainWindow window, IPointer pointer, Point at, KeyModifiers modifiers)
    {
        var target = window.FindControl<TextBlock>("CornerCounterText")!;
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, at, 0, props, modifiers) { RoutedEvent = InputElement.PointerPressedEvent });
    }

    internal static void Move(MainWindow window, IPointer pointer, Point at, KeyModifiers modifiers, bool button = true)
    {
        var target = window.FindControl<TextBlock>("CornerCounterText")!;
        var props = new PointerPointProperties(button ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None, PointerUpdateKind.Other);
        target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, pointer, window, at, 0, props, modifiers));
    }

    private static void Release(MainWindow window, IPointer pointer, Point at, KeyModifiers modifiers)
    {
        var target = window.FindControl<TextBlock>("CornerCounterText")!;
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, at, 0, props, modifiers, MouseButton.Left) { RoutedEvent = InputElement.PointerReleasedEvent });
    }

    private static async Task OnMainWindow(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            EnsureAppStyles();
            var window = new MainWindow();
            try
            {
                SetField(window, "_settings", new GlideSettingsState { ShowStatusSurface = true });
                window.Width = 1000; window.Height = 700;
                window.Show();
                window.FindControl<Grid>("ImageView")!.IsVisible = true;
                // The real picture viewport fills ImageView and receives the press; headless has no picture, so make it hit-testable.
                window.FindControl<Grid>("ImageView")!.Background = Avalonia.Media.Brushes.Transparent;
                Dispatcher.UIThread.RunJobs();
                Invoke(window, "ApplyStatusVisibility");
                Settle(window);
                body(window);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
            return Task.CompletedTask;
        });
        TempDir.TryDelete(root);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle); using (window.CaptureRenderedFrame()) { } }
    }

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private static object? Invoke(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!.Invoke(window, args);

    private static object? GetField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    private static void SetField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
}

