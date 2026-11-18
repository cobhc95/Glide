using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Settings;

namespace Glide.Explorer.Tests;

/// <summary>
/// Item 4 (5.0.6): a tooltip must not appear under the pointer while the user scrolls. Hovering a row of the Settings
/// Mouse gestures area rail starts the tooltip's show delay; a wheel turn before it expires used to leave that timer
/// running, so the tooltip popped up between two wheel ticks, right where the next tick then went (a tooltip is its own
/// native popup window).
/// </summary>
[Trait("Area", "Settings")]
public sealed class SettingsWheelToolTipTests
{
    private static async Task Settle(ExplorerHarness h, int rounds = 5)
    {
        for (var i = 0; i < rounds; i++) { h.Pump(); await Task.Delay(20); }
    }

    [Fact]
    public async Task A_wheel_turn_while_a_tooltip_is_pending_keeps_it_from_opening_under_the_pointer()
    {
        await ExplorerHarness.Run(async h =>
        {
            ToolTipGuard.Register();
            var w = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            w.Show();
            try
            {
                typeof(SettingsWindow).GetMethod("ShowCategory", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object?[] { "Mouse", true, false, null });
                var card = w.GetVisualDescendants().OfType<SettingsCard>().Single(c => c.CardId == "mouse-gestures");
                card.IsExpanded = true;
                await Settle(h, 10);
                var area = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("gestureArea") && b.IsEffectivelyVisible && b.Bounds.Height > 0);
                Assert.IsType<string>(ToolTip.GetTip(area));
                var p = area.TranslatePoint(new Point(area.Bounds.Width / 2, area.Bounds.Height / 2), w)!.Value;

                // Control: a plain hover opens the tooltip once the show delay has passed.
                var away = new Point(5, 5);
                w.MouseMove(away);
                await Settle(h, 3);
                await Task.Delay(500);
                w.MouseMove(p);
                for (var i = 0; i < 25 && !ToolTip.GetIsOpen(area); i++) { h.Pump(); await Task.Delay(40); }
                Assert.True(ToolTip.GetIsOpen(area), "hovering the area row did not open its tooltip");

                // The race: hover again and turn the wheel before the show delay expires, pointer still. The page is at the
                // top, so this wheel-up tick moves nothing (as at either end of any list): the pending tooltip used to open.
                w.MouseMove(away);
                await Settle(h, 3);
                await Task.Delay(500);
                w.MouseMove(p);
                h.Pump();
                await Task.Delay(120);
                var sv = w.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ContentScroll"); var off0 = sv.Offset;
                w.MouseWheel(p, new Vector(0, 1));
                for (var i = 0; i < 12; i++) { h.Pump(); await Task.Delay(40); }
                var open = w.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).Select(c => c.GetType().Name + " " + ToolTip.GetTip(c)).ToList();
                Assert.True(open.Count == 0, $"a tooltip opened right after the wheel turn (offset {off0} -> {sv.Offset}): " + string.Join(", ", open));
            }
            finally { w.Close(); }
        }, startExplorer: false);
    }

    [Fact]
    public void Tooltip_popup_windows_are_transparent_to_the_mouse_and_hand_on_the_wheel()
    {
        const uint WM_NCHITTEST = 0x0084, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_MOUSEMOVE = 0x0200, WM_MOUSEACTIVATE = 0x0021;
        Assert.Equal(ToolTipGuard.PopupMessageAction.HitTestTransparent, ToolTipGuard.ClassifyPopupMessage(WM_NCHITTEST));
        Assert.Equal(ToolTipGuard.PopupMessageAction.ForwardWheel, ToolTipGuard.ClassifyPopupMessage(WM_MOUSEWHEEL));
        Assert.Equal(ToolTipGuard.PopupMessageAction.ForwardWheel, ToolTipGuard.ClassifyPopupMessage(WM_MOUSEHWHEEL));
        Assert.Equal(ToolTipGuard.PopupMessageAction.NoActivate, ToolTipGuard.ClassifyPopupMessage(WM_MOUSEACTIVATE));
        Assert.Equal(ToolTipGuard.PopupMessageAction.Default, ToolTipGuard.ClassifyPopupMessage(WM_MOUSEMOVE));
    }
}
