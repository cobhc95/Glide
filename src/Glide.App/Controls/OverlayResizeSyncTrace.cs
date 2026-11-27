namespace Glide.App.Controls;

/// <summary>
/// Counts, for one Overlay resize drag, the size steps (WM_SIZE) and how many of them were presented before the next step began: a
/// step is "painted" when a WM_PAINT arrives after it. A step followed by another step with no paint in between was never presented
/// at its own size: that is the frame the compositor shows stretched or cropped (the flicker). The diagnostic trace writes these
/// counters when the drag ends (<c>overlay_window_resize_end</c>), so a flicker report can be measured instead of guessed.
/// </summary>
internal sealed class OverlayResizeSyncTrace
{
    private bool _awaitingPaint;
    public int Steps { get; private set; }
    public int Paints { get; private set; }
    public int UnpaintedSteps { get; private set; }

    // Cost of the drag (reported with the counters above at the end of the drag).
    /// <summary>Frame callbacks that applied a resize step (size, layout, chrome).</summary>
    public int Flushes { get; private set; }
    /// <summary>Chrome alignments (the chrome host / frame moved to the picture rectangle).</summary>
    public int ChromeAligns { get; private set; }
    /// <summary>SetWindowRgn calls made during the drag: 1 when the region was suspended at its start, never one per step.</summary>
    public int RegionApplies { get; private set; }
    /// <summary>Presented-frame sufficiency checks (a possible re-decode) held back until the drag ended.</summary>
    public int DeferredRefines { get; private set; }

    public void OnFlush() => Flushes++;
    public void OnChromeAligned() => ChromeAligns++;
    public void OnRegionApplied() => RegionApplies++;
    public void OnRefineDeferred() => DeferredRefines++;

    /// <summary>How many present requests (decodes / bitmap swaps) started between two request ids.</summary>
    public static long CountDelta(long now, long start) => Math.Max(0, now - start);

    public void OnSize()
    {
        Steps++;
        if (_awaitingPaint) UnpaintedSteps++;
        _awaitingPaint = true;
    }

    public void OnPaint()
    {
        if (!_awaitingPaint) return;
        Paints++;
        _awaitingPaint = false;
    }

    /// <summary>Percentage of steps that were superseded before being painted (0 when there were no steps).</summary>
    public double UnpaintedPercent => Steps == 0 ? 0 : Math.Round(100.0 * UnpaintedSteps / Steps, 1);
}
