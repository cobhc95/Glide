using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Glide.App.Controls;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.0.5: clicking the absolute top pixel of the title-bar "+" (maximized: the button is 44 DIP and touches the
/// screen edge) showed the hover / press visual but opened no tab, and the hover flickered along the top edge.
/// Driven with real pointer input through the headless platform on the themed MainWindow. Two Avalonia-level causes:
/// <list type="number">
/// <item>Fluent's Button:pressed applies scale(0.98); the pressed "+" pulled its top edge ~0.4 DIP below a pointer on
/// row 0, and Button raises Click only when the release point is still over the (scaled) button.</item>
/// <item>The rounded hit area (CornerRadius 5 on the "+", 8 on tabs) left the top-corner pixels to the title bar:
/// hover dropped there (flicker) and a press armed a window drag instead.</item>
/// </list>
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class TitleBarTopEdgeClickTests
{
    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public async Task The_plus_button_opens_a_tab_from_every_pixel_of_the_top_row(WindowState state)
    {
        await ExplorerHarness.Run(async h =>
        {
            EnterState(h, state);
            var plus = h.Window.FindControl<Button>("NewTabButton")!;
            Assert.IsType<EdgeHitButton>(plus);
            Assert.Equal(44, plus.Bounds.Height);
            Assert.Equal(0, plus.TranslatePoint(default, h.Window)!.Value.Y);

            var width = plus.Bounds.Width;
            foreach (var dx in new[] { 0.0, 0.4, 1, 2.5, width / 2, width - 2, width - 0.5 })
            {
                Relayout(h);
                var origin = plus.TranslatePoint(default, h.Window)!.Value;
                var point = new Point(origin.X + dx, 0);
                var hit = h.Window.InputHitTest(point) as Visual;
                Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Contains(plus), $"row 0 at +{dx} DIP hit {Describe(hit)}, not the + button");

                var before = h.Workspace.Tabs.Count;
                h.Click(point);
                await h.WaitUntil(() => h.Workspace.Tabs.Count == before + 1, $"a new tab from a click on row 0 at +{dx} DIP ({state})", 3000);
                await Task.Delay(560); // the next click at a nearby spot must not count as a double-click
            }
        }, startExplorer: false);
    }

    [Fact]
    public async Task A_pressed_title_bar_button_keeps_its_edge_under_the_pointer()
    {
        await ExplorerHarness.Run(h =>
        {
            EnterState(h, WindowState.Maximized);
            var plus = h.Window.FindControl<Button>("NewTabButton")!;
            var point = plus.TranslatePoint(new Point(plus.Bounds.Width / 2, 0), h.Window)!.Value;
            h.Window.MouseMove(point);
            h.Window.MouseDown(point, MouseButton.Left);
            h.Pump();
            try
            {
                Assert.True(plus.IsPressed);
                Assert.True(plus.RenderTransform is null || plus.RenderTransform.Value.IsIdentity,
                    $"the pressed + must not shrink away from the window edge (RenderTransform {plus.RenderTransform?.Value})");
                // The release check Button itself uses: the pointer is still over the button.
                Assert.Contains(plus.GetVisualsAt(h.Window.TranslatePoint(point, plus)!.Value), v => ReferenceEquals(v, plus) || plus.IsVisualAncestorOf(v));
            }
            finally { h.Window.MouseUp(point, MouseButton.Left); h.Pump(); }
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public async Task A_tab_is_selected_from_its_top_corner_pixels(WindowState state)
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Workspace.AddHome();
            h.Workspace.AddHome();
            EnterState(h, state);
            foreach (var corner in new[] { "left", "right" })
            {
                Relayout(h);
                var tabs = h.Window.FindControl<StackPanel>("TabStripPanel")!.Children.OfType<Border>().ToList();
                var target = tabs.First(t => !t.Classes.Contains("active"));
                var targetId = (Guid)target.DataContext!;
                Assert.Equal(44, target.Bounds.Height);
                var dx = corner == "left" ? 0.5 : target.Bounds.Width - 0.5;
                var point = target.TranslatePoint(new Point(dx, 0), h.Window)!.Value;
                var hit = h.Window.InputHitTest(point) as Visual;
                Assert.True(hit is StyledElement && hit.GetSelfAndVisualAncestors().Contains(target),
                    $"the {corner} top-corner pixel of a tab hit {Describe(hit)} (the title bar) instead of the tab");
                h.Click(point);
                await h.WaitUntil(() => h.Workspace.Active?.Id == targetId, $"tab selected from its {corner} top corner ({state})", 3000);
                await Task.Delay(560);
            }
        }, startExplorer: false);
    }

    [Fact]
    public async Task Empty_title_bar_and_the_strip_above_the_normal_plus_still_belong_to_the_title_bar()
    {
        await ExplorerHarness.Run(h =>
        {
            // Normal: the + is 36 DIP and centred, so the 4 DIP above it stay window-drag / resize space.
            EnterState(h, WindowState.Normal);
            var plus = h.Window.FindControl<Button>("NewTabButton")!;
            Assert.Equal(36, plus.Bounds.Height);
            var above = plus.TranslatePoint(new Point(plus.Bounds.Width / 2, -2), h.Window)!.Value;
            Assert.False(IsInteractive(h.Window.InputHitTest(above) as Visual), "the strip above the Normal-state + must stay title bar");

            // Maximized: empty title bar right of the + is still a drag / double-click-maximize area on row 0.
            EnterState(h, WindowState.Maximized);
            var empty = plus.TranslatePoint(new Point(plus.Bounds.Width + 30, 0), h.Window)!.Value;
            var hit = h.Window.InputHitTest(empty) as Visual;
            Assert.False(IsInteractive(hit), $"empty title bar at row 0 hit {Describe(hit)}");
            Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Any(v => v is StyledElement { Name: "ChromeBorder" }));
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    /// <summary>What the fix guards against: a stock rounded Fluent Button loses exactly these clicks.</summary>
    [Fact]
    public async Task A_stock_rounded_button_misses_its_top_corner_and_its_top_row_click()
    {
        await ExplorerHarness.Run(h =>
        {
            var clicks = 0;
            var stock = new Button { Width = 36, Height = 44, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Padding = default, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            stock.Click += (_, _) => clicks++;
            var window = new Window { Width = 200, Height = 120, Content = stock };
            window.Show();
            try
            {
                h.Pump(); window.UpdateLayout(); h.Pump();
                var corner = window.InputHitTest(new Point(0.5, 0)) as Visual;
                Assert.False(corner is not null && corner.GetSelfAndVisualAncestors().Contains(stock), "the rounded corner pixel is not part of a stock button");
                var top = new Point(18, 0);
                window.MouseMove(top);
                window.MouseDown(top, MouseButton.Left);
                h.Pump();
                Thread.Sleep(120); // let Fluent's 75 ms pressed transition finish
                h.Pump();
                window.MouseUp(top, MouseButton.Left);
                h.Pump();
                Assert.Equal(0, clicks);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }, startExplorer: false);
    }

    private static void EnterState(ExplorerHarness h, WindowState state)
    {
        h.Window.WindowState = state;
        h.Pump();
        h.Call("ApplyChromeLayoutForWindowState", new object?[] { null });
        Relayout(h);
        // Fullscreen auto-hide keeps the bar hidden until the pointer reaches the top edge.
        h.Window.FindControl<Border>("ChromeBorder")!.IsVisible = true;
        Relayout(h);
    }

    private static void Relayout(ExplorerHarness h)
    {
        h.Call("RebuildTabStrip");
        h.Window.UpdateLayout();
        h.Pump();
        h.Window.UpdateLayout();
        h.Pump();
    }

    private static bool IsInteractive(Visual? visual) =>
        visual is not null && visual.GetSelfAndVisualAncestors().Any(v => v is Button || v is StyledElement s && (s.Classes.Contains("tabHit") || s.Classes.Contains("glideTab")));

    private static string Describe(Visual? visual) =>
        visual is null ? "nothing" : string.Join(" < ", visual.GetSelfAndVisualAncestors().Take(4).Select(v => v.GetType().Name + (v is StyledElement { Name: { } n } ? "#" + n : "")));
}
