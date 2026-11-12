using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Layout regression for the status surface width/height. The bar must be exactly as wide/tall as
/// its real wrapped content: a single line must not reserve a phantom second row (which previously
/// made the bar noticeably larger than its contents).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class StatusBarLayoutTests
{
    public StatusBarLayoutTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Fact]
    public async Task One_line_status_content_does_not_reserve_a_second_row()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                ForceViewerStatusVisible(window);
                LayoutAt(window, 1800, 900);

                var surface = window.FindControl<Border>("ViewerStatusSurface")!;
                var row1 = window.FindControl<StackPanel>("StatusRow1")!;
                var row2 = window.FindControl<StackPanel>("StatusRow2")!;
                var stats = window.FindControl<TextBlock>("StatusStatsText")!;
                var windowControls = window.FindControl<StackPanel>("StatusWindowControls")!;

                // A wide window keeps stats, every icon group and the window buttons on a single row.
                Assert.False(row2.IsVisible, "the wide layout must not use a second row");
                Assert.Equal(0, Grid.GetRow(windowControls));
                Assert.True(surface.Bounds.Height <= row1.Bounds.Height + 20,
                    $"status surface reserved an extra row: surface={surface.Bounds.Width:F0}x{surface.Bounds.Height:F0}, row={row1.Bounds.Width:F0}x{row1.Bounds.Height:F0}");
                var maxExpectedWidth = stats.Bounds.Width + row1.Bounds.Width + windowControls.Bounds.Width + 60;
                Assert.True(surface.Bounds.Width <= maxExpectedWidth,
                    $"status surface wider than its content: surface={surface.Bounds.Width:F0}, content<= {maxExpectedWidth:F0}");
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Medium_width_moves_stats_to_a_header_and_balances_whole_groups_over_two_rows()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                ForceViewerStatusVisible(window);
                LayoutAt(window, 800, 700);

                var panel = window.FindControl<StackPanel>("StatusControlsPanel")!;
                var row1 = window.FindControl<StackPanel>("StatusRow1")!;
                var row2 = window.FindControl<StackPanel>("StatusRow2")!;
                var windowControls = window.FindControl<StackPanel>("StatusWindowControls")!;
                var more = window.FindControl<Border>("MoreGroupBorder")!;

                // Stats header + window buttons on row 0, icon groups below; nothing hidden.
                Assert.Equal(1, Grid.GetRow(panel));
                Assert.Equal(0, Grid.GetRow(windowControls));
                Assert.False(more.IsVisible, "nothing should overflow at this width");
                var rowOne = row1.Children.Count;
                var rowTwo = row2.IsVisible ? row2.Children.Count : 0;
                Assert.True(rowOne > 0);
                // Balanced: when two rows are used, neither is left with a lone orphan group.
                if (rowTwo > 0) Assert.True(rowTwo >= 2 && rowOne >= 2, $"unbalanced rows {rowOne}/{rowTwo}");
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Narrow_width_moves_low_priority_groups_into_the_overflow_menu()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                ForceViewerStatusVisible(window);
                LayoutAt(window, 240, 600);

                var row1 = window.FindControl<StackPanel>("StatusRow1")!;
                var row2 = window.FindControl<StackPanel>("StatusRow2")!;
                var more = window.FindControl<Border>("MoreGroupBorder")!;
                var nav = window.FindControl<Border>("NavGroupBorder")!;
                var overlay = window.FindControl<Border>("OverlayGroupBorder")!;

                Assert.True(more.IsVisible, $"the overflow menu button must appear when groups do not fit (window={window.Bounds.Width:F0}, row1={row1.Children.Count}, row1w={row1.Bounds.Width:F0}, row2={row2.IsVisible}, nav={nav.IsVisible}, overlay={overlay.IsVisible})");
                Assert.False(row2.IsVisible, "overflow uses a single row");
                Assert.True(nav.IsVisible, "navigation is never moved into the menu");
                Assert.False(overlay.IsVisible, "the lowest-priority group goes into the menu first");
                Assert.Contains(more, row1.Children);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    private static void ForceViewerStatusVisible(MainWindow window)
    {
        if (window.FindControl<Grid>("ImageView") is { } imageView) imageView.IsVisible = true;
        if (window.FindControl<Border>("ViewerStatusSurface") is { } surface) surface.IsVisible = true;
    }

    private static void LayoutAt(MainWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
    }
}
