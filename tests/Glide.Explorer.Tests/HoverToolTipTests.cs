using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Explorer.Tests;

/// <summary>
/// Tooltips proven with real (headless) pointer hover: the pointer is moved onto the element, the element under the
/// pointer is hit-tested, and the tooltip service must actually open the tooltip. Covers the Settings footer change
/// summary and the rows and category headings of the title-bar and status-bar customisers.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class HoverToolTipTests
{
    private static async Task Settle(ExplorerHarness h, int rounds = 5)
    {
        for (var i = 0; i < rounds; i++) { h.Pump(); await Task.Delay(20); }
    }

    /// <summary>Moves the pointer away (closing any tooltip), then walks it onto <paramref name="target"/> and waits past the show delay.</summary>
    private static async Task Hover(ExplorerHarness h, Window w, Point away, Point target)
    {
        w.MouseMove(away);
        await Settle(h, 3);
        await Task.Delay(500); // past BetweenShowDelay, so the next tooltip has to wait for the normal show delay
        for (var i = 1; i <= 8; i++) { w.MouseMove(away + (target - away) * (i / 8.0)); h.Pump(); await Task.Delay(10); }
        for (var i = 0; i < 20; i++) { h.Pump(); await Task.Delay(40); }
    }

    /// <summary>The control under the pointer whose tooltip the service would show: the hit element or its nearest ancestor with a tip.</summary>
    private static Control? TipHostAt(Window w, Point p, out Visual? hit)
    {
        hit = w.InputHitTest(p) as Visual;
        return hit?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => ToolTip.GetTip(c) is not null);
    }

    private static async Task AssertHoverShowsTip(ExplorerHarness h, Window w, Point away, Point p, Control expectedHost, string expectedTip, string where)
    {
        await Hover(h, w, away, p);
        var host = TipHostAt(w, p, out var hit);
        Assert.True(hit is InputElement { IsHitTestVisible: true }, $"{where}: nothing hit-testable under the pointer ({hit?.GetType().Name ?? "null"})");
        Assert.True(ReferenceEquals(expectedHost, host), $"{where}: the pointer is over {string.Join(" > ", hit!.GetSelfAndVisualAncestors().Take(6).Select(v => v.GetType().Name))}, not the tooltip host");
        Assert.Equal(expectedTip, ToolTip.GetTip(host!) as string);
        Assert.True(ToolTip.GetIsOpen(host!), $"{where}: the tooltip did not open on hover");
    }

    [Fact]
    public async Task Settings_footer_change_summary_shows_the_change_list_anywhere_over_the_block()
    {
        await ExplorerHarness.Run(async h =>
        {
            var w = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            w.Show();
            try
            {
                await Settle(h);
                var check = w.FindControl<CheckBox>("OverlayHighlightCheck")!;
                check.IsChecked = !(check.IsChecked ?? false);
                await Settle(h, 10);
                var host = w.FindControl<StackPanel>("DirtySummaryHost")!;
                var text = w.FindControl<TextBlock>("DirtyText")!;
                var hint = w.FindControl<TextBlock>("DirtyHintText")!;
                Assert.Equal("1 change", text.Text);
                Assert.True(hint.IsVisible);
                var popup = w.FindControl<Avalonia.Controls.Primitives.Popup>("DirtySummaryPopup")!;
                var tip = w.FindControl<TextBlock>("DirtySummaryPopupText")!.Text;
                Assert.False(string.IsNullOrWhiteSpace(tip));
                Assert.Contains("→", tip);
                Assert.Null(ToolTip.GetTip(host)); // own popup only: no second (ToolTip) popup
                Assert.False(text.IsHitTestVisible);
                Assert.False(hint.IsHitTestVisible);

                var away = new Point(400, 300);
                var onText = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), w)!.Value;
                var between = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height - 0.5), w)!.Value;
                var onHint = hint.TranslatePoint(new Point(hint.Bounds.Width / 2, hint.Bounds.Height / 2), w)!.Value;
                var hintLeft = hint.TranslatePoint(new Point(2, hint.Bounds.Height / 2), w)!.Value;
                foreach (var (p, where) in new[] { (onText, "\"1 change\" text"), (between, "between the lines"), (onHint, "hint line"), (hintLeft, "hint line, left end") })
                {
                    await Hover(h, w, away, p);
                    Assert.True(ReferenceEquals(host, (w.InputHitTest(p) as Visual)?.GetSelfAndVisualAncestors().FirstOrDefault(v => ReferenceEquals(v, host))), $"{where}: pointer not over the summary");
                    Assert.True(popup.IsOpen, $"{where}: the change list did not open on hover");
                    w.MouseMove(away);
                    await Settle(h, 3);
                    Assert.False(popup.IsOpen, $"{where}: the change list did not close when the pointer left");
                }

                // Click toggles it (keyboard / touch reach it the same way).
                w.MouseMove(away);
                await Settle(h, 3);
                w.MouseMove(onText); w.MouseDown(onText, MouseButton.Left); w.MouseUp(onText, MouseButton.Left);
                await Settle(h, 3);
                Assert.True(popup.IsOpen, "click did not open the change list");
                w.MouseDown(onText, MouseButton.Left); w.MouseUp(onText, MouseButton.Left);
                await Settle(h, 3);
                Assert.False(popup.IsOpen, "second click did not close the change list");
            }
            finally { w.Close(); }
        }, startExplorer: false);
    }

    [Fact]
    public async Task A_tooltip_left_open_is_closed_by_a_pointer_press_so_later_tooltips_still_open()
    {
        await ExplorerHarness.Run(async h =>
        {
            Glide.App.Services.ToolTipGuard.Register();
            var w = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            w.Show();
            try
            {
                await Settle(h);
                // Simulates a tip stuck open after its owner opened a modal dialog (the owner never sees the pointer leave).
                var owner = w.FindControl<Button>("RestoreDefaultsButton")!;
                ToolTip.SetIsOpen(owner, true);
                await Settle(h, 3);
                Assert.True(ToolTip.GetIsOpen(owner));
                var away = new Point(600, 300);
                w.MouseDown(away, MouseButton.Left); w.MouseUp(away, MouseButton.Left);
                await Settle(h, 3);
                Assert.False(ToolTip.GetIsOpen(owner), "a pointer press did not close the open tooltip");

                ToolTip.SetIsOpen(owner, true);
                await Settle(h, 3);
                Glide.App.Services.ToolTipGuard.CloseAllOpenToolTips();
                Assert.False(ToolTip.GetIsOpen(owner));
            }
            finally { w.Close(); }
        }, startExplorer: false);
    }

    [Fact]
    public async Task Customiser_rows_and_headings_show_their_full_name_on_hover()
    {
        await ExplorerHarness.Run(async h =>
        {
            // A long, trimmed name in the shown list ("Show in Explorer tab (select current)") and a few others.
            var windows = new Window[]
            {
                new TitleBarCustomizerWindow(new[] { "explorer", "rotate", "settings" }),
                new StatusBarCustomizerWindow(string.Empty)
            };
            foreach (var w in windows)
            {
                w.Show();
                try
                {
                    await Settle(h);
                    var lists = w.GetVisualDescendants().OfType<ListBox>().ToList();
                    Assert.Equal(2, lists.Count);
                    var checkedRows = 0; var checkedHeadings = 0;
                    foreach (var list in lists)
                    {
                        var items = list.GetVisualDescendants().OfType<ListBoxItem>()
                            .Where(i => i.Bounds.Height > 0 && i.TranslatePoint(new Point(0, i.Bounds.Height), list) is { } b && b.Y <= list.Bounds.Height)
                            .Take(4).ToList();
                        Assert.NotEmpty(items);
                        foreach (var item in items)
                        {
                            var label = item.GetVisualDescendants().OfType<TextBlock>().Last();
                            var expected = label.Text!;
                            var heading = item.Classes.Contains("customizerHeading");
                            var away = list.TranslatePoint(new Point(list.Bounds.Width / 2, -20), w)!.Value;
                            var points = new List<(Point, string)>
                            {
                                (label.TranslatePoint(new Point(Math.Min(20, label.Bounds.Width / 2), label.Bounds.Height / 2), w)!.Value, "on the name"),
                                (item.TranslatePoint(new Point(item.Bounds.Width * 0.7, item.Bounds.Height / 2), w)!.Value, "empty space right of the name (clear of the scroll bar)")
                            };
                            foreach (var (p, where) in points)
                                await AssertHoverShowsTip(h, w, away, p, item, expected, $"{w.GetType().Name} {(heading ? "heading" : "row")} '{expected}' {where}");
                            if (heading) checkedHeadings++; else checkedRows++;
                        }
                    }
                    Assert.True(checkedRows > 0, "no rows checked");
                    Assert.True(checkedHeadings > 0, "no category headings checked");
                    // Headings are hit-testable now, but still cannot be selected.
                    var firstHeading = lists[0].GetVisualDescendants().OfType<ListBoxItem>().First(i => i.Classes.Contains("customizerHeading"));
                    var hp = firstHeading.TranslatePoint(new Point(20, firstHeading.Bounds.Height / 2), w)!.Value;
                    w.MouseDown(hp, MouseButton.Left); w.MouseUp(hp, MouseButton.Left);
                    await Settle(h);
                    Assert.NotSame(firstHeading, lists[0].SelectedItem);
                }
                finally { w.Close(); }
            }
        }, startExplorer: false);
    }
    /// <summary>
    /// 5.1.7 UI test bug 6: a tooltip open on a Settings search result stayed on screen when the next query rebuilt the list
    /// (the pointer never "left" a control that no longer exists).
    /// </summary>
    [Fact]
    public async Task A_tooltip_open_on_a_search_result_closes_when_the_results_are_replaced_or_hidden()
    {
        await ExplorerHarness.Run(async h =>
        {
            Glide.App.Services.ToolTipGuard.Register();
            var w = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            w.Show();
            try
            {
                await Settle(h);
                void Rebuild(string query)
                {
                    w.FindControl<TextBox>("SearchBox")!.Text = query;
                    typeof(SettingsWindow).GetMethod("RunSearchRebuildCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, [query, false]);
                }
                Rebuild("tag groups");
                await Settle(h);
                var panel = w.FindControl<StackPanel>("SearchPanel")!;
                var open = panel.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Open");
                // Open as the tooltip service does after a hover (a real hover is timing-sensitive after other tests' tips).
                // Precondition only: in a full parallel run the tooltip service may close a programmatic tip once (pointer
                // state left by an earlier test), so reopen it a few times before checking what this test is about.
                for (var attempt = 0; attempt < 5 && !ToolTip.GetIsOpen(open); attempt++)
                {
                    ToolTip.SetIsOpen(open, true);
                    await Settle(h, 3);
                }
                Assert.True(ToolTip.GetIsOpen(open), "the result's tooltip is open");

                Rebuild("zoom");
                await Settle(h);
                Assert.False(ToolTip.GetIsOpen(open), "the tooltip of a removed result is still open");
                Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Control>(), c => ToolTip.GetIsOpen(c));

                // Hiding the list (Open reveals the setting on its page; Escape clears the search) must not leave a tip either.
                var second = panel.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Open");
                ToolTip.SetIsOpen(second, true);
                await Settle(h, 3);
                Assert.True(ToolTip.GetIsOpen(second));
                panel.IsVisible = false;
                await Settle(h);
                Assert.False(ToolTip.GetIsOpen(second), "a tooltip stays open over a hidden result list");
            }
            finally { w.Close(); }
        }, startExplorer: false);
    }

    /// <summary>Opens a.png in the tab, shows the text overlay and sets the Main tooltip delay; returns the overlay host.</summary>
    private static async Task<Border> OpenPictureWithOverlayAsync(ExplorerHarness h, int delayMs)
    {
        Glide.App.Services.ToolTipGuard.Register();
        h.DoubleClick("a.png");
        await h.WaitUntil(() => h.Workspace.Active is Glide.Core.Workspace.ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        var host = h.Window.FindControl<Border>("CornerCounterHost")!;
        h.Window.FindControl<TextBlock>("CornerCounterText")!.Text = "[1/3]  a.png";
        host.IsVisible = true;
        h.Settings.PictureCounterEnabled = true;
        TooltipAreas.SetDelayMs(h.Settings, TooltipAreas.Main, delayMs);
        await h.SettleAsync();
        for (var i = 0; i < 3; i++) { h.Pump(); await Task.Delay(20); }
        return host;
    }

    /// <summary>
    /// 5.1.7 UI test bug 8 (B1): hovering the text overlay shows "Alt+drag to move..." although the overlay is not hit-testable
    /// (so panning, the wheel and double-click still work under it). Since 5.2.0 the hint is a label of the window's own
    /// (not an Avalonia ToolTip, whose service closed it again on the real desktop).
    /// </summary>
    [Fact]
    public async Task Hovering_the_text_overlay_shows_its_hint_and_leaving_hides_it()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 50);
            Assert.False(host.IsHitTestVisible, "the overlay must stay transparent to the pointer (pan, wheel and double-click under it)");
            Assert.Null(ToolTip.GetTip(host));   // no tooltip on the overlay: the tooltip service would close it again

            var onOverlay = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            var away = new Point(onOverlay.X - Math.Min(200, onOverlay.X - 2), onOverlay.Y + 150);
            h.Window.MouseMove(away);
            h.Pump();
            for (var i = 1; i <= 6; i++) { h.Window.MouseMove(away + (onOverlay - away) * (i / 6.0)); h.Pump(); await Task.Delay(10); }
            await h.WaitUntil(() => h.Window.IsTextOverlayHintShown, "the overlay's hint showed on hover");

            h.Window.MouseMove(away);
            h.Pump();
            await h.WaitUntil(() => !h.Window.IsTextOverlayHintShown, "the hint went away when the pointer left the overlay");
        });
    }

    /// <summary>
    /// 5.2.0 desktop retest: the hint never showed on the real desktop. The overlay is not hit-testable, so the element under
    /// the pointer is the viewport, and the tooltip service (which tracks that element) closed a tip opened for another control.
    /// This test drives the real pointer-over tracking: the pointer is over the viewport, then over the overlay's rectangle, and
    /// the hint must be on screen after the delay and still there a frame later, next to (not on top of) the overlay, whatever
    /// the tooltip service does.
    /// </summary>
    [Fact]
    public async Task The_text_overlay_hint_survives_the_pointer_over_tracking_that_sees_the_viewport_under_the_overlay()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 120);
            var viewport = h.Window.FindControl<Control>("Viewport")!;
            var overlayCentre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            var inViewport = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 3, viewport.Bounds.Height / 2), h.Window)!.Value;

            // The pointer is over the viewport first, as when the user comes from the picture.
            h.Window.MouseMove(inViewport);
            h.Pump();
            Assert.True(h.Window.InputHitTest(inViewport) is Visual a && (ReferenceEquals(a, viewport) || viewport.IsVisualAncestorOf(a)), "the pointer is over the viewport");
            Assert.False(h.Window.IsTextOverlayHintShown);

            // ... then over the overlay's rectangle: still the viewport that is under the pointer (the overlay is not hit-testable).
            for (var i = 1; i <= 8; i++) { h.Window.MouseMove(inViewport + (overlayCentre - inViewport) * (i / 8.0)); h.Pump(); await Task.Delay(8); }
            var under = h.Window.InputHitTest(overlayCentre) as Visual;
            Assert.True(under is not null && !ReferenceEquals(under, host) && !host.IsVisualAncestorOf(under), "the overlay is transparent to hit-testing, the pointer is over something else");

            await h.WaitUntil(() => h.Window.IsTextOverlayHintShown, "the hint is on screen after the delay");
            // A frame later, and after what the real tooltip machinery does on every pointer move / press, it is still there.
            for (var i = 0; i < 4; i++)
            {
                h.Window.MouseMove(new Point(overlayCentre.X + (i % 2), overlayCentre.Y));
                Glide.App.Services.ToolTipGuard.CloseAllOpenToolTips();
                h.Pump();
                await Task.Delay(20);
                Assert.True(h.Window.IsTextOverlayHintShown, $"the hint disappeared {i + 1} frame(s) after it showed");
            }

            // It is a themed label next to the overlay, inside the window, with the text.
            var hint = h.Window.FindControl<Border>("TextOverlayHint")!;
            Assert.True(hint.Bounds.Width > 20 && hint.Bounds.Height > 10, $"the hint has a real size ({hint.Bounds})");
            Assert.Equal(Glide.App.MainWindow.TextOverlayToolTip, ((TextBlock)hint.Child!).Text);
            Assert.False(hint.IsHitTestVisible);
            Assert.NotNull(hint.Background);    // themed like the tooltips: the theme's ToolTipBackground / ToolTipBorderBrush / ToolTipForeground
            Assert.NotNull(hint.BorderBrush);
            Assert.NotNull(((TextBlock)hint.Child!).Foreground);
            var hintBox = new Rect(hint.TranslatePoint(new Point(0, 0), h.Window)!.Value, hint.Bounds.Size);
            var hostBox = new Rect(host.TranslatePoint(new Point(0, 0), h.Window)!.Value, host.Bounds.Size);
            Assert.False(hintBox.Intersects(hostBox), $"the hint {hintBox} covers the overlay {hostBox}");
            Assert.True(new Rect(h.Window.Bounds.Size).Contains(hintBox), $"the hint {hintBox} is outside the window {h.Window.Bounds}");
            Assert.DoesNotContain(h.Window.GetVisualDescendants().OfType<ToolTip>(), t => t.Content as string == Glide.App.MainWindow.TextOverlayToolTip);

            // Leaving for the viewport hides it.
            h.Window.MouseMove(inViewport);
            h.Pump();
            Assert.False(h.Window.IsTextOverlayHintShown, "the hint is still shown with the pointer over the viewport");
        });
    }

    [Fact]
    public async Task A_click_or_the_wheel_over_the_text_overlay_hides_its_hint_and_a_click_keeps_it_away_until_the_pointer_comes_back()
    {
        await ExplorerHarness.Run(async h =>
        {
            var host = await OpenPictureWithOverlayAsync(h, 60);
            var centre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            var away = new Point(centre.X - 150, centre.Y + 150);
            h.Window.MouseMove(away);
            h.Pump();
            h.Window.MouseMove(centre);
            await h.WaitUntil(() => h.Window.IsTextOverlayHintShown, "the hint shows");

            // A click hides it and it stays away while the pointer rests.
            h.Click(centre);
            Assert.False(h.Window.IsTextOverlayHintShown, "the hint stays after a click");
            for (var i = 0; i < 12; i++) { h.Window.MouseMove(new Point(centre.X + (i % 2), centre.Y)); h.Pump(); await Task.Delay(20); }
            Assert.False(h.Window.IsTextOverlayHintShown, "the hint came back although the pointer never left after the click");

            // Leaving and coming back shows it again.
            h.Window.MouseMove(away);
            h.Pump();
            h.Window.MouseMove(centre);
            await h.WaitUntil(() => h.Window.IsTextOverlayHintShown, "the hint shows again after the pointer came back");

            // A wheel tick hides it at once.
            h.Window.MouseWheel(centre, new Vector(0, 1));
            h.Pump();
            Assert.False(h.Window.IsTextOverlayHintShown, "the hint stays over a wheel tick");
        });
    }

    [Theory]
    [InlineData(900, 100, 120, 20, 1000, 700)]   // box in the top-right corner: the hint ends at its right edge, below it
    [InlineData(10, 100, 120, 20, 1000, 700)]    // box at the left
    [InlineData(500, 690, 120, 20, 1000, 700)]   // box at the very bottom: the hint goes above
    [InlineData(990, 0, 100, 20, 1000, 700)]     // box half outside the window
    public void The_text_overlay_hint_is_placed_next_to_the_box_and_always_inside_the_window(double x, double y, double w, double h, double boundsW, double boundsH)
    {
        var host = new Rect(x, y, w, h);
        var tip = new Size(260, 30);
        var at = Glide.App.TextOverlayHintPlacement.Place(host, tip, new Size(boundsW, boundsH));
        var box = new Rect(at, tip);
        Assert.True(new Rect(0, 0, boundsW, boundsH).Contains(box), $"{box} is outside the window");
        if (new Rect(0, 0, boundsW, boundsH).Intersects(host)) Assert.False(box.Intersects(host), $"{box} covers {host}");
    }
}
