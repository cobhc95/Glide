using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml.Styling;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Layout contract of the Settings window on small displays: compact default and minimum size, a fixed tight
/// navigation column, a content area that fills the window (no fixed gutter on its right), a footer that stays
/// visible at the minimum size, and no horizontal overflow at 720 DIP wide. Windows are rendered with the real
/// app styles (Fluent + GlideTheme). Set GLIDE_SAVE_RENDERS to also write PNG frames.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingsWindowLayoutTests : IDisposable
{
    public SettingsWindowLayoutTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly List<IStyle> InstalledStyles = new();

    /// <summary>
    /// The real app styles (Fluent + GlideTheme) are installed on the application, like App.axaml does, BEFORE the window is
    /// created: styles added to a window that already exists do not re-theme controls that were styled during construction
    /// (the search box and category filter then render without a template).
    /// </summary>
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

    private static SettingsWindow Open(double width, double height, string page = "General", bool expandAll = false, GlideSettingsState? state = null)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(state ?? new GlideSettingsState(), (_, _) => { });
        window.Show();
        Call(window, "ShowCategory", page, true, false, null!);
        if (expandAll) Call(window, "ExpandAllCardsClicked", null!, new Avalonia.Interactivity.RoutedEventArgs());
        Resize(window, width, height);
        return window;
    }

    private static void Resize(SettingsWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static object? Call(SettingsWindow window, string name, params object[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Named<T>(SettingsWindow window, string name) where T : Visual =>
        window.GetVisualDescendants().OfType<T>().Single(c => (c as StyledElement)?.Name == name);

    private static Rect BoundsIn(Visual visual, Visual root)
    {
        var m = visual.TransformToVisual(root) ?? throw new InvalidOperationException("Not in the same tree.");
        return new Rect(visual.Bounds.Size).TransformToAABB(m);
    }

    private static string? SaveDirectory => Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    private static void Save(SettingsWindow window, string name, double scale = 1.0)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    [Fact]
    public async Task Default_and_minimum_size_are_compact()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            Assert.Equal(930, SettingsWindow.DefaultWidth);
            Assert.Equal(630, SettingsWindow.DefaultHeight);
            Assert.Equal(720, SettingsWindow.MinimumWidth);
            Assert.Equal(480, SettingsWindow.MinimumHeight);
            // Smaller than the previous 1040 x 760 default, horizontally and vertically.
            Assert.True(SettingsWindow.DefaultWidth < 1040 && SettingsWindow.DefaultHeight < 760);

            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            Assert.Equal(SettingsWindow.DefaultWidth, window.Width);
            Assert.Equal(SettingsWindow.DefaultHeight, window.Height);
            Assert.Equal(SettingsWindow.MinimumWidth, window.MinWidth);
            Assert.Equal(SettingsWindow.MinimumHeight, window.MinHeight);
            Assert.True(window.CanResize);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(1366, 768, 1.0)]
    [InlineData(1366, 768, 1.25)]
    [InlineData(1366, 768, 1.5)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(2560, 1440, 1.5)]
    public void Initial_size_never_exceeds_ninety_percent_of_the_working_area(int pixelWidth, int pixelHeight, double scale)
    {
        var workWidth = pixelWidth / scale;
        var workHeight = pixelHeight / scale;
        var (size, minimum) = SettingsWindow.ComputeInitialSize(workWidth, workHeight);
        Assert.True(size.Width <= workWidth * 0.9 + 0.01, $"{size.Width} > 90% of {workWidth}");
        Assert.True(size.Height <= workHeight * 0.9 + 0.01, $"{size.Height} > 90% of {workHeight}");
        Assert.True(size.Width <= SettingsWindow.DefaultWidth && size.Height <= SettingsWindow.DefaultHeight);
        Assert.True(minimum.Width <= size.Width && minimum.Height <= size.Height);
        Assert.True(minimum.Width <= SettingsWindow.MinimumWidth && minimum.Height <= SettingsWindow.MinimumHeight);
        // Roomy displays keep the default.
        if (workWidth * 0.9 >= SettingsWindow.DefaultWidth && workHeight * 0.9 >= SettingsWindow.DefaultHeight)
            Assert.Equal(new Size(SettingsWindow.DefaultWidth, SettingsWindow.DefaultHeight), size);
    }

    [Fact]
    public void Placement_keeps_the_window_inside_the_working_area_and_centred_when_it_fits()
    {
        var work = new PixelRect(0, 0, 1366, 728);
        // Fits: centred on the owner exactly.
        var centred = SettingsWindow.PlaceWithin(work, new PixelPoint(683, 364), new PixelSize(930, 630));
        Assert.Equal(new PixelPoint(218, 49), centred);
        // Owner near the bottom-right corner: shifted back inside.
        var shifted = SettingsWindow.PlaceWithin(work, new PixelPoint(1300, 700), new PixelSize(930, 630));
        Assert.Equal(1366 - 930, shifted.X);
        Assert.Equal(728 - 630, shifted.Y);
        // Owner off the top-left: shifted to the corner of the working area (e.g. taskbar on the left/top).
        var corner = SettingsWindow.PlaceWithin(new PixelRect(48, 0, 1318, 728), new PixelPoint(0, 0), new PixelSize(930, 630));
        Assert.Equal(new PixelPoint(48, 0), corner);
    }

    [Fact]
    public async Task FitToWorkingArea_shrinks_on_a_1366x768_display_and_stays_on_screen_centred_over_the_owner()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var owner = new Window { Width = 800, Height = 500, Position = new PixelPoint(300, 150) };
            owner.Show();
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show(owner);
            try
            {
                // 1366 x 768 at 150 % DPI with the taskbar taken off: 911 x 485 DIP usable.
                var work = new PixelRect(0, 0, 1366, 728);
                window.FitToWorkingArea(work, 1.5);
                var maxW = work.Width / 1.5 * 0.9;
                var maxH = work.Height / 1.5 * 0.9;
                Assert.True(window.Width <= maxW + 0.01, $"{window.Width} > {maxW}");
                Assert.True(window.Height <= maxH + 0.01, $"{window.Height} > {maxH}");
                Assert.True(window.MinWidth <= window.Width && window.MinHeight <= window.Height);
                var rect = new PixelRect(window.Position, new PixelSize((int)Math.Ceiling(window.Width * 1.5), (int)Math.Ceiling(window.Height * 1.5)));
                Assert.True(rect.X >= work.X && rect.Y >= work.Y && rect.Right <= work.Right && rect.Bottom <= work.Bottom, $"window {rect} is not inside {work}");
            }
            finally { window.Close(); owner.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FitToWorkingArea_leaves_a_roomy_display_alone()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var before = window.Position;
                window.FitToWorkingArea(new PixelRect(0, 0, 5120, 2880), 1.0);
                Assert.Equal(SettingsWindow.DefaultWidth, window.Width);
                Assert.Equal(SettingsWindow.DefaultHeight, window.Height);
                Assert.Equal(before, window.Position);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(930, 630)]
    [InlineData(1100, 760)]
    [InlineData(720, 480)]
    public async Task Content_fills_the_window_with_no_fixed_gutter_on_its_right(double width, double height)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, height);
            try
            {
                var root = Named<Grid>(window, "ReferenceRoot");
                Assert.True(root.ColumnDefinitions[0].Width.IsAbsolute);
                Assert.Equal(SettingsWindow.NavigationColumnWidth, root.ColumnDefinitions[0].Width.Value);
                Assert.True(root.ColumnDefinitions[1].Width.IsStar);

                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var scrollBounds = BoundsIn(scroll, root);
                Assert.Equal(root.Bounds.Width, scrollBounds.Right, 0.5);
                Assert.Equal(SettingsWindow.NavigationColumnWidth, scrollBounds.X, 0.5);
                Assert.Equal(root.ColumnDefinitions[1].ActualWidth, scroll.Bounds.Width, 0.5);

                // Padding is symmetrical and modest: 16-24 DIP on both sides of the page, header row and footer.
                var page = (Control)scroll.Content!;
                Assert.InRange(page.Margin.Left, 16, 24);
                Assert.InRange(page.Margin.Right, 16, 24);
                var search = Named<TextBox>(window, "SearchBox");
                var searchHost = (Grid)search.Parent!;
                Assert.InRange(searchHost.Margin.Left, 16, 24);
                Assert.InRange(searchHost.Margin.Right, 16, 24);
                Assert.Equal(root.Bounds.Width - 20, BoundsIn(searchHost, root).Right, 1.5);
                // The first card/page title starts at the same left edge as the search box.
                Assert.Equal(BoundsIn(search, root).X, BoundsIn(Named<TextBlock>(window, "PageTitle"), root).X, 1.0);

                // Header rows are compact (the old 82 DIP band had ~30 DIP of dead space under the search row).
                Assert.InRange(root.RowDefinitions[0].Height.Value, 48, 72);
                Assert.InRange(root.RowDefinitions[2].Height.Value, 60, 84);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(720, 480)]
    [InlineData(720, 600)]
    [InlineData(900, 480)]
    [InlineData(930, 630)]
    [InlineData(1100, 760)]
    public async Task Footer_buttons_stay_fully_visible_and_header_does_not_overflow(double width, double height)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, height, "Mouse");
            try
            {
                var area = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var scrollBounds = BoundsIn(scroll, window);
                foreach (var text in new[] { "Restore defaults", "Cancel", "Apply", "OK" })
                {
                    var button = text == "Restore defaults"
                        ? Named<Button>(window, "RestoreDefaultsButton")
                        : window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, text));
                    var rect = BoundsIn(button, window);
                    Assert.True(button.IsEffectivelyVisible, $"'{text}' is not visible");
                    Assert.True(area.Contains(rect.TopLeft) && area.Contains(rect.BottomRight - new Vector(0.5, 0.5)), $"'{text}' {rect} is outside the window {area}");
                    Assert.True(rect.Top >= scrollBounds.Bottom - 0.5, $"'{text}' overlaps the page content");
                }

                // The footer text cannot collide with the buttons.
                var version = Named<TextBlock>(window, "AboutVersionText");
                var versionRect = BoundsIn(version, window);
                Assert.True(versionRect.Right <= BoundsIn(window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "OK")), window).Left);

                // Header row (search + category filter) and the page title row with Expand/Collapse all.
                var search = BoundsIn(Named<TextBox>(window, "SearchBox"), window);
                var combo = BoundsIn(Named<ComboBox>(window, "SearchCategoryCombo"), window);
                Assert.True(search.Width >= 160, $"search box is only {search.Width} wide");
                Assert.True(search.Right <= combo.Left + 0.5, "search box overlaps the category filter");
                Assert.True(combo.Right <= width - 16, $"category filter {combo} runs past the right padding");
                var expand = BoundsIn(Named<Button>(window, "ExpandAllCardsButton"), window);
                var collapse = BoundsIn(Named<Button>(window, "CollapseAllCardsButton"), window);
                var title = BoundsIn(Named<TextBlock>(window, "PageTitle"), window);
                Assert.True(expand.Right <= collapse.Left + 0.5, "Expand all overlaps Collapse all");
                Assert.True(collapse.Right <= width - 16, $"Collapse all {collapse} runs past the right padding");
                Assert.True(title.Right <= expand.Left + 0.5, "the page title runs under Expand all");
                Assert.True(expand.Height <= 38);

                Save(window, $"layout-mouse-{width}x{height}.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static readonly Type[] LeafTypes = { typeof(Button), typeof(ComboBox), typeof(TextBox), typeof(NumericUpDown), typeof(CheckBox), typeof(ListBox), typeof(ToggleButton) };

    private static List<string> HorizontalOverflow(SettingsWindow window)
    {
        var scroll = Named<ScrollViewer>(window, "ContentScroll");
        var limit = scroll.Bounds.Width + 0.5;
        var problems = new List<string>();
        foreach (var visual in scroll.GetVisualDescendants())
        {
            if (visual is not Control control || !control.IsEffectivelyVisible) continue;
            if (!LeafTypes.Any(t => t.IsInstanceOfType(control))) continue;
            // The page's own scrollbar sits at the viewport edge by design.
            if (control.FindAncestorOfType<ScrollBar>() is not null) continue;
            // Parts of a composite control (a TextBox inside a NumericUpDown, buttons of a ComboBox, ...) are covered by the control itself.
            if (control.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, scroll)).Any(a => a is NumericUpDown or ComboBox or ListBox or Button or CheckBox or ToggleButton or TextBox)) continue;
            var rect = BoundsIn(control, scroll);
            if (rect.Width < 1) continue;
            if (rect.Right > limit - 12) problems.Add($"{control.GetType().Name} '{control.Name ?? (control as ContentControl)?.Content ?? (control as TextBox)?.Watermark}' right edge {rect.Right:0.#} > {limit - 12:0.#}");
        }
        return problems;
    }

    /// <summary>Round 4 E1: "Hide cursor in normal window" lives on Mouse &amp; keyboard (catalogue and control) with a link to the fullscreen option.</summary>
    [Fact]
    public async Task Hide_cursor_in_normal_window_is_on_the_mouse_page()
    {
        Assert.Equal("Mouse & keyboard", Glide.Core.Settings.SettingsCatalog.All.Single(e => e.Id == "fullscreen.hideCursorWindowed").Category);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(900, 700, "Mouse", expandAll: true);
            try
            {
                var mouse = window.FindControl<StackPanel>("MousePanel")!;
                Assert.Contains(window.FindControl<CheckBox>("HideCursorNormalWindowCheck")!.GetLogicalAncestors(), a => ReferenceEquals(a, mouse));
                Assert.Contains(window.FindControl<Button>("HideCursorFullscreenLinkButton")!.GetLogicalAncestors(), a => ReferenceEquals(a, mouse));
                Assert.DoesNotContain(window.FindControl<CheckBox>("HideCursorFullscreenCheck")!.GetLogicalAncestors(), a => ReferenceEquals(a, mouse));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>Round 4 D3: the flipping "Key to open ... instead" labels never run into their combo box, whichever wording shows.</summary>
    [Theory]
    [InlineData(0, 0, 720)]
    [InlineData(1, 1, 720)]
    [InlineData(0, 0, 1000)]
    [InlineData(1, 1, 1000)]
    public async Task New_tab_key_labels_keep_clear_of_their_combo_boxes(int position, int side, double width)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, 600, "Tabs", expandAll: true);
            try
            {
                Named<ComboBox>(window, "NewTabPositionCombo").SelectedIndex = position;
                Named<ComboBox>(window, "SwitchToNewTabsCombo").SelectedIndex = side;
                Call(window, "UpdateNewTabKeyLabels");
                Resize(window, width, 600);
                foreach (var (label, combo) in new[] { ("NewTabPositionKeyLabel", "NewTabPositionKeyCombo"), ("NewTabForegroundKeyLabel", "NewTabForegroundKeyCombo") })
                {
                    var text = Named<TextBlock>(window, label);
                    var textRect = BoundsIn(text, window);
                    var comboRect = BoundsIn(Named<ComboBox>(window, combo), window);
                    var ink = new Rect(textRect.X, textRect.Y, text.TextLayout.WidthIncludingTrailingWhitespace, text.TextLayout.Height);
                    var sideBySide = ink.Bottom > comboRect.Top && ink.Top < comboRect.Bottom;
                    Assert.True(sideBySide ? ink.Right <= comboRect.X - 8 : !ink.Intersects(comboRect),
                        $"{label} \"{text.Text}\" text {ink} runs into its combo {comboRect} at {width} wide");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("General")]
    [InlineData("Appearance")]
    [InlineData("Interface")]
    [InlineData("Tabs")]
    [InlineData("Mouse")]
    [InlineData("Slideshow")]
    [InlineData("Performance")]
    [InlineData("Windows")]
    public async Task No_page_overflows_horizontally_at_720_wide(string page)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(720, 600, page, expandAll: true);
            try
            {
                var problems = HorizontalOverflow(window);
                Save(window, $"overflow-{page}-720x600.png");
                Assert.True(problems.Count == 0, $"{page} overflows at 720 wide:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Gesture_panel_and_hotkey_list_stay_usable_at_720_wide()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(720, 600, "Mouse", expandAll: true);
            try
            {
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var host = Named<Border>(window, "GestureMatrixHost");
                var hostRect = BoundsIn(host, scroll);
                Assert.True(hostRect.Right <= scroll.Bounds.Width, "the gesture card runs past the page");
                foreach (var picker in window.GetVisualDescendants().OfType<GestureActionPicker>().Where(p => p.IsEffectivelyVisible))
                {
                    var rect = BoundsIn(picker, scroll);
                    Assert.True(rect.Width >= 140, $"a gesture dropdown is only {rect.Width:0} wide");
                    Assert.True(rect.Right <= scroll.Bounds.Width - 12, $"a gesture dropdown runs past the page ({rect.Right:0})");
                }

                var list = Named<ListBox>(window, "HotkeyList");
                var listRect = BoundsIn(list, scroll);
                Assert.True(listRect.Right <= scroll.Bounds.Width - 12, "the shortcut list runs past the page");
                Assert.True(listRect.Width >= 300, "the shortcut list is too narrow to read");
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(list));
                Save(window, "gestures-hotkeys-720x600.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Render matrix for visual review (only when GLIDE_SAVE_RENDERS is set). GLIDE_RENDER_TAG names the run
    /// (before / after) so two runs can sit side by side in one folder.
    /// </summary>
    [Fact]
    public async Task Render_matrix_for_review()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        var tag = Environment.GetEnvironmentVariable("GLIDE_RENDER_TAG") ?? "after";
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            foreach (var page in new[] { "General", "Mouse" })
            {
                foreach (var (w, h) in new[] { (720d, 480d), (720d, 600d), (720d, 760d), (900d, 480d), (900d, 600d), (900d, 760d), (1100d, 480d), (1100d, 600d), (1100d, 760d) })
                {
                    var window = Open(w, h, page);
                    try { Save(window, $"{tag}-{page}-{w}x{h}.png"); }
                    finally { window.Close(); }
                }
                var defaultSize = (Environment.GetEnvironmentVariable("GLIDE_RENDER_DEFAULT") ?? "").Split('x');
                var def = defaultSize.Length == 2
                    ? Open(double.Parse(defaultSize[0]), double.Parse(defaultSize[1]), page)
                    : Open(SettingsWindow.DefaultWidth, SettingsWindow.DefaultHeight, page);
                try
                {
                    // Default size, as the app opens it, at 150 % to match the owner's screenshots.
                    Save(def, $"{tag}-{page}-default-150pct.png", 1.5);
                    Call(def, "ExpandAllCardsClicked", null!, new Avalonia.Interactivity.RoutedEventArgs());
                    Dispatcher.UIThread.RunJobs();
                    def.UpdateLayout();
                    Save(def, $"{tag}-{page}-default-expanded-150pct.png", 1.5);
                }
                finally { def.Close(); }
            }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(720, 480)]
    [InlineData(930, 630)]
    public async Task Footer_Restore_defaults_and_Reset_Glide_are_two_equal_buttons_whose_labels_fit(double width, double height)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, height, "General");
            try
            {
                var area = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
                var left = Named<Grid>(window, "SettingsFooterLeft");
                var restore = Named<Button>(window, "RestoreDefaultsButton");
                var reset = Named<Button>(window, "OpenResetGlideButton");
                var r = BoundsIn(restore, window);
                var g = BoundsIn(reset, window);
                var footer = BoundsIn(left, window);
                // Same row, equal widths, ~8 DIP apart, together exactly the width the single button had (the footer column).
                Assert.Equal(r.Top, g.Top, 1);
                Assert.Equal(r.Width, g.Width, 1);
                Assert.InRange(g.Left - r.Right, 6, 10);
                Assert.Equal(footer.Left, r.Left, 1);
                Assert.Equal(footer.Right, g.Right, 1);
                Assert.Equal(196, footer.Width, 1);
                Assert.True(r.Height <= 34 && g.Height <= 34);
                // The footer row is no taller than before.
                Assert.Equal(76, ((Grid)window.Content!).RowDefinitions[2].Height.Value);
                foreach (var (button, rect) in new[] { (restore, r), (reset, g) })
                {
                    Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
                    Assert.True(button.Focusable && button.IsTabStop, $"{button.Name} must be reachable with Tab");
                    Assert.True(area.Contains(rect.TopLeft) && area.Contains(rect.BottomRight - new Vector(0.5, 0.5)), $"{button.Name} {rect} is outside the window {area}");
                    // The label (icon + text) fits inside the button: nothing is clipped.
                    var content = (Control)button.Content!;
                    content.Measure(Size.Infinity);
                    var inner = rect.Width - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right;
                    Assert.True(content.DesiredSize.Width <= inner + 0.5, $"{button.Name}: label needs {content.DesiredSize.Width} DIP, has {inner}");
                    Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
                }
                Assert.Equal("More reset options: history, window positions, hotkeys, caches, Windows integration", ToolTip.GetTip(reset));
                Save(window, $"footer-reset-buttons-{width}x{height}.png");
                Save(window, $"footer-reset-buttons-{width}x{height}@150.png", 1.5);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Footer_Reset_Glide_button_clears_the_search_opens_the_card_scrolls_to_it_and_focuses_it()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(720, 480, "General");
            try
            {
                // A search with a category filter is active and the card is collapsed.
                Named<ComboBox>(window, "SearchCategoryCombo").SelectedIndex = 1;
                var search = Named<TextBox>(window, "SearchBox");
                search.Text = "zoom";
                Call(window, "RunSearchRebuildCore", "zoom", false);
                Dispatcher.UIThread.RunJobs();
                var card = window.GetLogicalDescendants().OfType<Glide.App.Controls.SettingsCard>().Single(c => c.CardId == SettingsWindow.ResetCardId);
                card.IsExpanded = false;

                var button = Named<Button>(window, "OpenResetGlideButton");
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                // The headless dispatcher does not lay out between jobs on its own; the real one lays out before the
                // Loaded-priority reveal runs.
                for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

                Assert.True(string.IsNullOrEmpty(search.Text));
                Assert.False(Named<StackPanel>(window, "SearchPanel").IsVisible);
                Assert.True(Named<StackPanel>(window, "WindowsPanel").IsVisible);
                Assert.True(card.IsExpanded);
                var viewport = BoundsIn(Named<ScrollViewer>(window, "ContentScroll"), window);
                var header = BoundsIn(card.Header, window);
                Assert.True(header.Top >= viewport.Top - 0.5 && header.Bottom <= viewport.Bottom + 0.5, $"card header {header} is not in view {viewport}");
                var first = Named<CheckBox>(window, "ResetAllSettingsCheck");
                Assert.True(first.IsFocused, "the first Reset Glide tick box has keyboard focus");
                Assert.False(first.IsChecked == true, "opening the card never ticks anything");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>5.1.7 UI test bug 1: both offset boxes of the Text overlay placement cards stay inside the card, on either card.</summary>
    [Theory]
    [InlineData(false, 720)]
    [InlineData(true, 720)]
    [InlineData(false, 930)]
    [InlineData(true, 930)]
    public async Task Text_overlay_offset_boxes_stay_inside_their_placement_card(bool sideTabs, double width)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, 700, "Appearance", expandAll: true);
            try
            {
                Call(window, "PlaceTextOverlayOffsetPanel", sideTabs);
                Resize(window, width, 700);
                var card = Named<Border>(window, sideTabs ? "SideTabsPlacementCard" : "TopTabsPlacementCard");
                var cardRect = BoundsIn(card, window);
                foreach (var name in new[] { "PictureOffsetXBox", "PictureOffsetYBox" })
                {
                    var box = Named<NumericUpDown>(window, name);
                    var rect = BoundsIn(box, window);
                    Assert.True(rect.Width >= 100, $"{name} is only {rect.Width:0} wide");
                    // Real layout: the text area inside the box shows at least 6 characters ("-12345") of the control's own font.
                    var text = box.GetVisualDescendants().OfType<TextBox>().First();
                    var inner = BoundsIn(text, window);
                    var sample = new Avalonia.Media.FormattedText("-12345", System.Globalization.CultureInfo.CurrentCulture, Avalonia.Media.FlowDirection.LeftToRight,
                        new Avalonia.Media.Typeface(text.FontFamily), text.FontSize, null);
                    Assert.True(inner.Width - text.Padding.Left - text.Padding.Right >= sample.Width, $"{name}: the text area is {inner.Width:0} wide, -12345 needs {sample.Width:0}");
                    Assert.True(rect.Right <= cardRect.Right - card.Padding.Right + 0.5, $"{name} right edge {rect.Right:0.#} sticks out past the card's content edge {cardRect.Right - card.Padding.Right:0.#}");
                    Assert.True(rect.Left >= cardRect.Left + card.Padding.Left - 0.5, $"{name} left edge is outside the card");
                }
                Save(window, $"offset-card-{(sideTabs ? "side" : "top")}-{width:0}.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 5.1.7 desktop retest E: typing 5000 into an offset box (real key input, then leaving the box) is stored as 5000, not 500 or 4000;
    /// the limit is applied when the overlay is drawn, never to what was typed.
    /// </summary>
    [Theory]
    [InlineData(false, "PictureOffsetXBox", "5000", 5000, 0)]
    [InlineData(true, "PictureOffsetYBox", "-12345", 0, -12345)]
    [InlineData(false, "PictureOffsetXBox", "100000", 100000, 0)]
    public async Task Typing_into_an_offset_box_is_stored_exactly(bool sideTabs, string name, string typed, int expectedX, int expectedY)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState { TabsEnabled = true, TabStripPosition = sideTabs ? "Side" : "Top" };
            var window = Open(930, 700, "Appearance", expandAll: true, state: state);
            try
            {
                Call(window, "PlaceTextOverlayOffsetPanel", sideTabs);
                Resize(window, 930, 700);
                var box = Named<NumericUpDown>(window, name);
                var text = box.GetVisualDescendants().OfType<TextBox>().First();
                text.Focus();
                Dispatcher.UIThread.RunJobs();
                text.SelectAll();
                window.KeyTextInput(typed);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(typed, text.Text);   // every character was accepted by the box
                Named<TextBox>(window, "SearchBox").Focus();   // leaving the box commits it
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal((decimal)(name == "PictureOffsetXBox" ? expectedX : expectedY), box.Value);
                var read = (GlideSettingsState)Call(window, "ReadControls")!;
                Assert.Equal((expectedX, expectedY), Glide.Core.Settings.TextOverlayPlacements.Offset(read, sideTabs));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>5.1.7 UI test bug 2: a search result's title is never cut off ("New-tab custom pat"); it wraps instead.</summary>
    [Theory]
    [InlineData("New-tab custom path", 720)]
    [InlineData("New-tab custom path", 930)]
    [InlineData("Startup custom path", 720)]
    public async Task Search_result_titles_are_not_cut_off(string query, double width)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(width, 600, "General");
            try
            {
                Named<TextBox>(window, "SearchBox").Text = query;
                Call(window, "RunSearchRebuildCore", query, false);
                Resize(window, width, 600);
                var panel = Named<StackPanel>(window, "SearchPanel");
                var title = panel.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => string.Equals(t.Text, query, StringComparison.OrdinalIgnoreCase) && t.FontWeight == Avalonia.Media.FontWeight.SemiBold);
                Assert.True(title.TextLayout.WidthIncludingTrailingWhitespace <= title.Bounds.Width + 0.5,
                    $"the title \"{title.Text}\" needs {title.TextLayout.WidthIncludingTrailingWhitespace:0} but is given {title.Bounds.Width:0}");
                Save(window, $"search-title-{width:0}.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
    /// <summary>5.1.7 UI test bug 5: a search result has exactly one Open button (Tag groups showed a second, inert one).</summary>
    [Fact]
    public async Task Every_search_result_has_at_most_one_Open_button_and_Tag_groups_one_that_works()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(900, 700, "General");
            try
            {
                var offenders = new List<string>();
                foreach (var setting in Glide.Core.Settings.SettingsCatalog.All)
                {
                    var card = (Control)Call(window, "BuildSearchResult", setting)!;
                    var opens = card.GetLogicalDescendants().OfType<Button>().Count(b => b.Content as string == "Open");
                    if (opens > 1) offenders.Add($"{setting.Id}: {opens}");
                }
                Assert.True(offenders.Count == 0, "more than one Open button on: " + string.Join(", ", offenders));

                var tags = (Control)Call(window, "BuildSearchResult", Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "tags.groups"))!;
                Assert.Single(tags.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "Open");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
