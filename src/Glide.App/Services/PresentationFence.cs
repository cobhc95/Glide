using System.Runtime.InteropServices;
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
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

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

        void Finish(bool rendered)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            if (Dispatcher.UIThread.CheckAccess())
            {
                viewport.PresentationDrawRecorded -= handler;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { viewport.PresentationDrawRecorded -= handler; } catch { }
                }, DispatcherPriority.Send);
            }
            registration.Dispose();
            fenceCts.Dispose();
            completion.TrySetResult(rendered);
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
                else if (OperatingSystem.IsWindows())
                {
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("dwm_flush_waiting");
                    await Task.Run(() => DwmFlush(), fenceCts.Token).ConfigureAwait(false);
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("dwm_flush_done");
                    rendered = true;
                }
                else
                {
                    var visual = ElementComposition.GetElementVisual(viewport);
                    if (visual is null) { Finish(false); return; }
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("compositor_batch_request_start");
                    var batch = visual.Compositor.RequestCompositionBatchCommitAsync();
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("compositor_batch_waiting");
                    await batch.Rendered.WaitAsync(_timeout, fenceCts.Token).ConfigureAwait(false);
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("compositor_batch_rendered_done");
                    rendered = true;
                }
                Finish(rendered);
            }
            catch (OperationCanceledException) { Finish(false); }
            catch (TimeoutException) { Finish(false); }
            catch { Finish(false); }
        }

        viewport.PresentationDrawRecorded += handler;
        registration = fenceCts.Token.Register(() =>
        {
            Dispatcher.UIThread.Post(() => Finish(false), DispatcherPriority.Background);
        });
        return completion.Task;
    }
}
