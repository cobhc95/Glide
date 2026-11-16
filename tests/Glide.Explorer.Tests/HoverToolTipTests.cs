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
}
