using Glide.App.Controls;
using Glide.App.Services;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PresentationFenceTests
{
    [Fact]
    public async Task Matching_draw_waits_for_controlled_render_completion()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fence = new PresentationFence(async (_, token) =>
            {
                entered.TrySetResult(true);
                return await release.Task.WaitAsync(token);
            });
            var viewport = new ImageViewport();
            var task = fence.ArmAsync(viewport, 42, CancellationToken.None);

            viewport.RecordPresentationDrawForTests(41);
            Assert.False(task.IsCompleted);
            viewport.RecordPresentationDrawForTests(42);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(task.IsCompleted);

            release.SetResult(true);
            Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(1)));
        });
    }

    [Fact]
    public async Task No_draw_or_compositor_progress_times_out_as_not_presented()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var fence = new PresentationFence((_, _) => Task.FromResult(true), TimeSpan.FromMilliseconds(40));
            var viewport = new ImageViewport();
            var task = fence.ArmAsync(viewport, 99, CancellationToken.None);

            Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(1)));
        });
    }

    [Fact]
    public async Task Cancellation_before_render_completion_never_accepts_frame()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fence = new PresentationFence(async (_, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
            var viewport = new ImageViewport();
            using var cts = new CancellationTokenSource();
            var task = fence.ArmAsync(viewport, 7, cts.Token);
            viewport.RecordPresentationDrawForTests(7);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cts.Cancel();
            Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(1)));
        });
    }
}
