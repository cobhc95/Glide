using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>
/// The Explorer toolbar's Show in File Explorer button: left-click is Windows File Explorer; right- and middle-click no
/// longer launch anything (right-click opens a target menu built by the same helper as the title-bar locate menu).
/// Launches go through the recording seams (ExplorerHost.RevealInWindows / LaunchAlternativeFileManager).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerFileManagerButtonTests
{
    private static string HeaderText(MenuItem item) => item.Header switch
    {
        StackPanel panel => panel.Children.OfType<TextBlock>().First().Text ?? "",
        var other => other?.ToString() ?? ""
    };

    [Fact]
    public async Task Right_and_middle_click_launch_nothing_and_left_click_reveals_in_Windows()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alternative = new List<string>();
            var previous = ExplorerHost.LaunchAlternativeFileManager;
            ExplorerHost.LaunchAlternativeFileManager = (exe, args) => { alternative.Add(args); return true; };
            try
            {
                h.Settings.AlternativeFileManagerPath = @"C:\Tools\TotalCmd64.exe";
                var button = h.Surface.WindowsExplorerButton;
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), h.Window)!.Value;

                h.Click(point, MouseButton.Middle);
                h.Click(point, MouseButton.Right);
                h.Call("CloseActiveOwnedMenu", new object?[] { null });
                h.Pump();
                Assert.Empty(alternative);
                Assert.Empty(h.Revealed);

                h.Click(point, MouseButton.Left);
                await h.WaitUntil(() => h.Revealed.Count == 1, "left-click reveal");
                Assert.Empty(alternative);

                Assert.Null(typeof(Glide.App.Controls.BrowserSurface).GetEvent("AlternativeFileManagerRequested"));
                h.Call("RefreshDynamicTooltips"); // the tooltip names the program set above
                var tip = ToolTip.GetTip(button)?.ToString() ?? "";
                // 5.0.6: the tooltip names the right-click menu (it opens a menu, it launches nothing); middle-click does nothing.
                Assert.Contains("Right-click for more: Show in TotalCmd64", tip, StringComparison.Ordinal);
                Assert.DoesNotContain("Middle-click", tip, StringComparison.OrdinalIgnoreCase);
            }
            finally { ExplorerHost.LaunchAlternativeFileManager = previous; }
        });
    }

    [Fact]
    public async Task Title_bar_locate_button_tooltip_names_its_right_click_menu()
    {
        await ExplorerHarness.Run(async h =>
        {
            var buttons = h.Get<Dictionary<string, Button>>("_titleBarButtons");
            Assert.True(buttons.TryGetValue("file.locate", out var locate), "locate button on the title bar");
            h.Settings.AlternativeFileManagerPath = "";
            h.Call("RefreshTitleActionTooltips");
            Assert.Contains("Right-click for more", ToolTip.GetTip(locate!)?.ToString() ?? "", StringComparison.Ordinal);
            h.Settings.AlternativeFileManagerPath = @"C:\Tools\TotalCmd64.exe";
            h.Call("RefreshTitleActionTooltips");
            Assert.Contains("Show in TotalCmd64", ToolTip.GetTip(locate!)?.ToString() ?? "", StringComparison.Ordinal);
            // Other title buttons keep a single line.
            Assert.DoesNotContain("Right-click", ToolTip.GetTip(buttons["nav.back"])?.ToString() ?? "", StringComparison.Ordinal);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Context_menu_lists_Windows_and_the_alternative_and_runs_them()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alternative = new List<string>();
            var previous = ExplorerHost.LaunchAlternativeFileManager;
            ExplorerHost.LaunchAlternativeFileManager = (exe, args) => { alternative.Add(exe); return true; };
            try
            {
                List<MenuItem> Items()
                {
                    // The menu rebuilds its items on each opening; attaching again builds them for the current settings.
                    h.Call("AttachExplorerToolbarRevealMenu", h.Surface.WindowsExplorerButton);
                    return h.Surface.WindowsExplorerButton.ContextMenu!.ItemsSource!.Cast<MenuItem>().ToList();
                }

                var items = Items();
                Assert.Equal(new[] { "Show in Windows File Explorer", "Show in other file manager…" }, items.Select(HeaderText));

                h.Settings.AlternativeFileManagerPath = @"C:\Tools\TotalCmd64.exe";
                items = Items();
                Assert.Equal(new[] { "Show in Windows File Explorer", "Show in TotalCmd64" }, items.Select(HeaderText));
                Assert.DoesNotContain(items, i => HeaderText(i).Contains('✓'));

                items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                h.Pump();
                Assert.Equal(new[] { @"C:\Tools\TotalCmd64.exe" }, alternative);

                items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await h.WaitUntil(() => h.Revealed.Count == 1, "Windows reveal");
            }
            finally { ExplorerHost.LaunchAlternativeFileManager = previous; }
        });
    }
}
