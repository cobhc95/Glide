using System.Runtime;
using Avalonia.Threading;

namespace Glide.App;

/// <summary>
/// 5.1.1 RAM (docs/audits/ram-audit-5.1.1.md #5): when the window is minimised Glide gives memory back while keeping
/// the picture on screen. The neighbour frames, reduced frames of other images and the compressed file bytes are dropped
/// (<see cref="Glide.Imaging.ImageLoadCoordinator.TrimForBackground"/>), the managed heap is compacted once and the
/// working set is trimmed. The current frame and the warm tab frames (already fit-sized) are kept, so restoring shows
/// the same picture and tab switching stays instant; the neighbours are prepared again right after restore.
/// "Purge cache on minimise" keeps its full purge.
/// </summary>
public partial class MainWindow
{
    private bool _trimmedForBackground;

    private void TrimMemoryForBackground()
    {
        try
        {
            var released = _loader.TrimForBackground();
            _trimmedForBackground = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != Avalonia.Controls.WindowState.Minimized) return;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                if (OperatingSystem.IsWindows()) SetProcessWorkingSetSize((IntPtr)(-1), (IntPtr)(-1), (IntPtr)(-1)); // -1: this process
            }, DispatcherPriority.Background);
            _diagnostics.Write("window", "trimmed_for_background", new { released });
        }
        catch (Exception ex) { _diagnostics.Write("window", "trim_for_background_failed", new { error = ex.GetType().Name }); }
    }

    /// <summary>Restored after a background trim: prepare the neighbours again at once.</summary>
    private void RestoreAfterBackgroundTrim()
    {
        if (!_trimmedForBackground) return;
        _trimmedForBackground = false;
        if (_presentedPath is not null) ScheduleNavigationNeighbours();
    }
}
