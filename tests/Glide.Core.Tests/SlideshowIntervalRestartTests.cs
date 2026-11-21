using Avalonia;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Services;

namespace Glide.Core.Tests;

/// <summary>5.1.5: a newly shown image restarts the full slideshow interval; a paused session stays paused.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SlideshowIntervalRestartTests
{
    [Fact]
    public void RestartInterval_RestartsRunningTimer_AndKeepsPausedOrStoppedSessionsIdle()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var timer = new DispatcherTimer();
            var interval = 3000;
            using var slideshow = new SlideshowSessionController(
                timer, () => true, () => 5, () => 0, () => false, () => true, () => false, () => 1,
                () => false, () => interval, () => Task.FromResult(true),
                () => new ViewerViewSnapshot("Fit", 1, default(Vector)), _ => { }, () => false, () => { },
                _ => Task.FromResult(false), _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
                () => Task.CompletedTask, () => { }, (_, _) => { });

            slideshow.RestartInterval();   // not active: no-op
            Assert.False(timer.IsEnabled);

            slideshow.ToggleAsync().GetAwaiter().GetResult();
            Assert.True(slideshow.IsRunning);
            interval = 4500;
            slideshow.RestartInterval();
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromMilliseconds(4500), timer.Interval);

            slideshow.Pause();
            slideshow.RestartInterval();   // manual navigation while paused: stays paused
            Assert.False(timer.IsEnabled);
            Assert.False(slideshow.IsRunning);
            Assert.True(slideshow.IsActive);

            slideshow.Stop(restoreStartingMode: false);
            slideshow.RestartInterval();
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void PresentCurrent_RestartsSlideshowIntervalWhenTheImageChanges()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AGENTS.md"))) root = Path.GetDirectoryName(root)!;
        var text = File.ReadAllText(Path.Combine(root, "src", "Glide.App", "MainWindow.Navigation.cs"));
        Assert.Contains("if (imageChanged) _slideshow?.RestartInterval();", text);
    }
}
