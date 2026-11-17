using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Services;

namespace Glide.Explorer.Tests;

/// <summary>
/// Tab tooltips keep the placement the tab strip gives them: beside the tab for side (vertical) tabs, under the tab
/// for the top strip with hover previews. ToolTipGuard's automatic top/bottom placement used to overwrite it every
/// time a tooltip opened, so side tab tooltips covered the neighbouring tabs.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
public sealed class TabToolTipPlacementTests
{
    private static Border TabBorder(ExplorerHarness h, Guid id) =>
        h.Get<StackPanel>("TabStripPanel").Children.OfType<Border>().Single(b => Equals(b.DataContext, id));

    /// <summary>Walks the real pointer onto the tab, waits for its tooltip to open, and returns the tab.</summary>
    private static async Task<Border> HoverTabAsync(ExplorerHarness h, Guid id)
    {
        var away = new Point(900, 500);
        h.Window.MouseMove(away);
        ToolTipGuard.CloseAllOpenToolTips();
        for (var i = 0; i < 15; i++) { h.Pump(); await Task.Delay(40); }
        var tab = TabBorder(h, id);
        var target = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;
        for (var i = 1; i <= 8; i++) { h.Window.MouseMove(away + (target - away) * (i / 8.0)); h.Pump(); await Task.Delay(10); }
        await h.WaitUntil(() => ToolTip.GetIsOpen(TabBorder(h, id)), "tab tooltip opened on hover", 4000);
        return TabBorder(h, id);
    }

    private static async Task RebuildAsync(ExplorerHarness h)
    {
        h.Call("ApplyChromeLayoutForWindowState", new object?[] { null });
        for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
        h.Call("RebuildTabStrip");
        for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
    }

    [Fact]
    public async Task Side_tab_tooltips_open_beside_the_tab_and_top_tabs_with_previews_open_below_it()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // the middle-clicked tab opens in the background (the default is foreground since 5.0.6)
            ToolTipGuard.Register(); // as at application start-up
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.Count == 3, "background tab opened");
            var id = h.Workspace.Tabs[h.Workspace.ActiveIndex + 1].Id;

            // Top strip with hover previews: under the tab.
            Assert.True(h.Settings.TabHoverPreviews);
            var top = await HoverTabAsync(h, id);
            Assert.Equal(PlacementMode.Bottom, ToolTip.GetPlacement(top));

            // Side tabs: beside the tab (the side panel is on the left, so the tip opens to its right).
            h.Settings.TabStripPosition = "Side";
            await RebuildAsync(h);
            Assert.True((bool)h.Get<object>("SideTabsActive"));
            var side = await HoverTabAsync(h, id);
            Assert.True(ToolTip.GetPlacement(side) is PlacementMode.Right or PlacementMode.Left,
                $"side tab tooltip placement was {ToolTip.GetPlacement(side)}, not beside the tab");

            // Side tabs without hover previews: still beside the tab.
            h.Settings.TabHoverPreviews = false;
            await RebuildAsync(h);
            side = await HoverTabAsync(h, id);
            Assert.True(ToolTip.GetPlacement(side) is PlacementMode.Right or PlacementMode.Left,
                $"side tab tooltip placement (previews off) was {ToolTip.GetPlacement(side)}, not beside the tab");
        });
    }

    /// <summary>
    /// 5.0.6 bug: tooltips (tab hover previews and every other tip) did not appear until the user clicked Glide again,
    /// because ToolTipGuard cancelled every tooltip while the window was inactive (another app in front, or right after a
    /// menu, drag or dialog). An inactive window that is enabled and has no modal dialog open shows its tooltips.
    /// </summary>
    [Fact]
    public async Task An_inactive_window_without_a_modal_dialog_still_opens_tooltips()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // the middle-clicked tab opens in the background (the default is foreground since 5.0.6)
            ToolTipGuard.Register(); // as at application start-up
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.Count == 3, "background tab opened");
            var id = h.Workspace.Tabs[h.Workspace.ActiveIndex + 1].Id;

            // The user switches to another app: Glide is deactivated (enabled, no dialog).
            // (the headless platform's own deactivation callback; IWindowBaseImpl.Deactivated is not public)
            var impl = h.Window.PlatformImpl!;
            var deactivated = impl.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .First(p => p.Name.EndsWith("Deactivated", StringComparison.Ordinal) && p.PropertyType == typeof(Action));
            ((Action?)deactivated.GetValue(impl))?.Invoke();
            for (var i = 0; i < 3; i++) { h.Pump(); await Task.Delay(20); }
            Assert.False(h.Window.IsActive);
            Assert.False(ToolTipGuard.IsBlockedByModal(h.Window));

            var tab = await HoverTabAsync(h, id);
            Assert.True(ToolTip.GetIsOpen(tab));
            Assert.False(h.Window.IsActive); // it opened without a click re-activating the window

            // A disabled owner (what ShowDialog does) still blocks tips, so none floats over the dialog.
            h.Window.IsEnabled = false;
            try { Assert.True(ToolTipGuard.IsBlockedByModal(h.Window)); }
            finally { h.Window.IsEnabled = true; }
        });
    }
}
