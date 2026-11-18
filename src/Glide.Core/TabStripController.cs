namespace Glide.Core;

/// <summary>
/// Stage D step 4: tab-strip state and decisions that used to live in <c>MainWindow.Tabs.cs</c> — Ctrl+Tab
/// most-recently-used cycling, overflow arrows and scroll step, the neighbour slide during a drag, and the tear-off
/// threshold. Pure (ids, numbers and rectangles), so the behaviour is tested without a window; MainWindow keeps the
/// controls and applies the results.
/// </summary>
public sealed class TabStripController
{
    private readonly List<Guid> _mru = new();
    private List<Guid>? _cycleSnapshot;
    private int _cycleIndex;

    /// <summary>Most-recently-used order (front = most recent).</summary>
    public IReadOnlyList<Guid> Mru => _mru;

    /// <summary>True while a held-Ctrl MRU cycle is in progress.</summary>
    public bool Cycling => _cycleSnapshot is not null;

    /// <summary>Syncs the MRU list with the open tabs and moves the active one to the front.</summary>
    public void Touch(IEnumerable<Guid> tabs, Guid? active)
    {
        var ids = tabs as IReadOnlyCollection<Guid> ?? tabs.ToList();
        var set = ids.ToHashSet();
        _mru.RemoveAll(id => !set.Contains(id));
        foreach (var id in ids)
            if (!_mru.Contains(id)) _mru.Add(id);
        if (active is { } a)
        {
            _mru.Remove(a);
            _mru.Insert(0, a);
        }
    }

    /// <summary>Touch, unless an MRU cycle is in progress (the landed-on tab is committed when Ctrl is released).</summary>
    public void TouchUnlessCycling(IEnumerable<Guid> tabs, Guid? active)
    {
        if (!Cycling) Touch(tabs, active);
    }

    /// <summary>Releasing Ctrl ends an MRU cycle and commits the landed-on tab as most recent.</summary>
    public void EndCycle(IEnumerable<Guid> tabs, Guid? active)
    {
        if (_cycleSnapshot is null) return;
        _cycleSnapshot = null;
        Touch(tabs, active);
    }

    /// <summary>
    /// One MRU step. While Ctrl is held the snapshot taken at the first press is walked; without Ctrl each press is one
    /// complete switch (the caller then touches the new active tab with <see cref="Touch"/>).
    /// </summary>
    public Guid NextInCycle(IEnumerable<Guid> tabs, Guid? active, int delta, bool holdingCtrl)
    {
        if (_cycleSnapshot is null)
        {
            Touch(tabs, active);
            _cycleSnapshot = _mru.ToList();
            _cycleIndex = 0;
        }
        var snapshot = _cycleSnapshot;
        _cycleIndex = ((_cycleIndex + delta) % snapshot.Count + snapshot.Count) % snapshot.Count;
        var target = snapshot[_cycleIndex];
        if (!holdingCtrl) _cycleSnapshot = null;
        return target;
    }

    /// <summary>Which overflow arrows show for the strip's scroll state.</summary>
    public static (bool Left, bool Right) OverflowArrows(bool enabled, double viewport, double extent, double offset)
    {
        if (!enabled) return (false, false);
        var maxOffset = Math.Max(0, extent - Math.Max(0, viewport));
        if (maxOffset <= 1) return (false, false);
        var x = Math.Clamp(offset, 0, maxOffset);
        return (x > 1, x < maxOffset - 1);
    }

    /// <summary>Arrows are hidden for side tabs, tabs off, arrows off, or a window narrower than 330 DIP.</summary>
    public static bool OverflowArrowsAllowed(bool sideTabs, bool tabsEnabled, bool arrowsSetting, double windowWidth) =>
        !sideTabs && tabsEnabled && arrowsSetting && windowWidth >= 330;

    /// <summary>The new offset after one overflow-arrow click.</summary>
    public static double ScrollOffset(double viewport, double extent, double offset, int direction, double tabMaxWidth)
    {
        viewport = Math.Max(1, viewport);
        var maxOffset = Math.Max(0, extent - viewport);
        var step = Math.Max(90, Math.Min(Math.Max(tabMaxWidth, 120) + 3, viewport * 0.65));
        return Math.Clamp(offset + direction * step, 0, maxOffset);
    }

    /// <summary>
    /// How far each neighbour slides while a tab is dragged <paramref name="draggedOffset"/> along the strip: the first
    /// neighbour starts yielding after about a quarter slot and arrives by three-quarters (smoothstep); later ones follow.
    /// Returns one offset per tab (the dragged tab's own entry is 0; it is drawn under the pointer by the caller).
    /// </summary>
    public static double[] NeighbourOffsets(int draggedIndex, int count, double draggedOffset, double slot)
    {
        var offsets = new double[count];
        if (draggedIndex < 0 || draggedIndex >= count) return offsets;
        slot = Math.Max(1, slot);
        var travelled = draggedOffset / slot;
        static double Smooth(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t * t * (3 - 2 * t);
        }
        if (travelled > 0)
        {
            for (var i = draggedIndex + 1; i < count; i++)
            {
                var progress = Smooth((travelled - (i - draggedIndex - 0.75)) / 0.5);
                if (progress > 0) offsets[i] = -slot * progress;
            }
        }
        else if (travelled < 0)
        {
            for (var i = draggedIndex - 1; i >= 0; i--)
            {
                var progress = Smooth((-travelled - (draggedIndex - i - 0.75)) / 0.5);
                if (progress > 0) offsets[i] = slot * progress;
            }
        }
        return offsets;
    }

    /// <summary>
    /// Tear-off decision: outside the window always tears off; otherwise the pointer must leave the strip by a generous
    /// slack (sideways for side tabs) so a sloppy reorder never detaches the tab.
    /// </summary>
    public static bool BeyondTearOffThreshold(bool insideWindow, bool sideTabs, int x, int y,
        int stripX, int stripY, int stripWidth, int stripHeight, double renderScale)
    {
        if (!insideWindow) return true;
        if (renderScale <= 0) renderScale = 1.0;
        var tolerance = Math.Max(48, (int)Math.Round(64 * renderScale));
        if (sideTabs) return x < stripX - tolerance || x > stripX + stripWidth + tolerance;
        return y < stripY - tolerance || y > stripY + stripHeight + tolerance;
    }
}
