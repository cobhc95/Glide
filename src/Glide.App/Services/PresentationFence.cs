using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.Core;

namespace Glide.App.Services;

/// <summary>
/// Arms before bitmap assignment and completes only after ImageViewport records the matching draw and
/// the compositor reports the subsequently requested composition batch as Rendered.  The optional
/// post-draw awaiter is a deterministic regression seam: tests can hold/release the exact boundary
/// without substituting a dispatcher callback for rendering.
/// </summary>
internal sealed class PresentationFence
{
    private readonly Func<ImageViewport, CancellationToken, Task<bool>>? _postDrawAwaiterForTests;
    private readonly TimeSpan _timeout;

    internal PresentationFence(Func<ImageViewport, CancellationToken, Task<bool>>? postDrawAwaiterForTests = null, TimeSpan? timeoutForTests = null)
    {
        _postDrawAwaiterForTests = postDrawAwaiterForTests;
        _timeout = timeoutForTests ?? TimeSpan.FromSeconds(2);
    }

    public Task<bool> ArmAsync(ImageViewport viewport, long requestId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fenceCts.CancelAfter(_timeout);
        CancellationTokenRegistration registration = default;
        Action<long>? handler = null;
        var finished = 0;

        void FinishOnUi(bool rendered)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            viewport.PresentationDrawRecorded -= handler;
            registration.Dispose();
            fenceCts.Dispose();
            completion.TrySetResult(rendered);
        }

        async Task FinishOnUiAsync(bool rendered)
        {
            if (Dispatcher.UIThread.CheckAccess())
                FinishOnUi(rendered);
            else
                await Dispatcher.UIThread.InvokeAsync(() => FinishOnUi(rendered), DispatcherPriority.Render);
        }

        handler = drawnRequestId =>
        {
            if (drawnRequestId != requestId || Volatile.Read(ref finished) != 0) return;
            // One matching draw owns this fence. Unsubscribe immediately on the UI thread so later
            // repaints cannot create a second compositor wait for the same request.
            viewport.PresentationDrawRecorded -= handler;
            if (GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("viewport_draw_recorded", $"request={requestId}");
            _ = CompleteAfterCompositionAsync();
        };

        async Task CompleteAfterCompositionAsync()
        {
            try
            {
                fenceCts.Token.ThrowIfCancellationRequested();
                bool rendered;
                if (_postDrawAwaiterForTests is not null)
                {
                    rendered = await _postDrawAwaiterForTests(viewport, fenceCts.Token).ConfigureAwait(false);
                }
                else
                {
                    var visual = ElementComposition.GetElementVisual(viewport);
                    if (visual is null) { await FinishOnUiAsync(false); return; }
                    var batch = visual.Compositor.RequestCompositionBatchCommitAsync();
                    await batch.Rendered.WaitAsync(_timeout, fenceCts.Token).ConfigureAwait(false);
                    rendered = true;
                }
                await FinishOnUiAsync(rendered).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { await FinishOnUiAsync(false).ConfigureAwait(false); }
            catch (TimeoutException) { await FinishOnUiAsync(false).ConfigureAwait(false); }
            catch { await FinishOnUiAsync(false).ConfigureAwait(false); }
        }

        viewport.PresentationDrawRecorded += handler;
        registration = fenceCts.Token.Register(() =>
        {
            Dispatcher.UIThread.Post(() => FinishOnUi(false), DispatcherPriority.Background);
        });
        return completion.Task;
    }
}
