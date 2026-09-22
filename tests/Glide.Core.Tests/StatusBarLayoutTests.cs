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
                LayoutAt(window, 1400, 900);

                var surface = window.FindControl<Border>("ViewerStatusSurface")!;
                var panel = window.FindControl<WrapPanel>("StatusControlsPanel")!;
                var stats = window.FindControl<TextBlock>("StatusStatsText")!;

                // Default configuration at 1400 px wide puts the controls on a single row.
                Assert.True(panel.Bounds.Height <= panel.DesiredSize.Height + 1,
                    $"expected a single control row, panel={panel.Bounds.Width:F0}x{panel.Bounds.Height:F0}");
                // The surface must only be one row taller than the controls (its padding), never two.
                Assert.True(surface.Bounds.Height <= panel.Bounds.Height + 20,
                    $"status surface reserved an extra row: surface={surface.Bounds.Width:F0}x{surface.Bounds.Height:F0}, panel={panel.Bounds.Width:F0}x{panel.Bounds.Height:F0}");
                // And it must hug the content horizontally (stats + controls + small padding).
                var maxExpectedWidth = stats.Bounds.Width + panel.Bounds.Width + 40;
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
