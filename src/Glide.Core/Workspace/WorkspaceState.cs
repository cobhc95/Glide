namespace Glide.Core.Workspace;

/// <summary>Pure tab/workspace model. No Avalonia types are allowed here.</summary>
public sealed class WorkspaceState
{
    private readonly List<TabState> _tabs = new();
    private readonly Stack<TabState> _closed = new();
    public int ClosedHistoryLimit { get; set; } = 20;
    public IReadOnlyList<TabState> Tabs => _tabs;
    public int ActiveIndex { get; private set; } = -1;
    public TabState? Active => ActiveIndex >= 0 && ActiveIndex < _tabs.Count ? _tabs[ActiveIndex] : null;
    public int ClosedCount => _closed.Count;

    public WorkspaceState(bool createHome = true)
    {
        if (createHome) AddHome();
    }

    public TabState AddHome() => Add(new HomeTabState(Guid.NewGuid()));
    public TabState AddImage(string path) => Add(new ImageTabState(Guid.NewGuid(), Path.GetFullPath(path)));
    public TabState AddBrowser(string folder) => Add(new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(folder)));

    public TabState? DuplicateActive() => Active switch
    {
        HomeTabState => AddHome(),
        ImageTabState image => AddImage(image.Path),
        BrowserTabState browser => AddBrowser(browser.Folder),
        _ => null
    };

    public bool ReplaceActiveImage(string path)
    {
        if (Active is not ImageTabState image) return false;
        _tabs[ActiveIndex] = image with { Path = Path.GetFullPath(path), Title = Path.GetFileName(path) };
        return true;
    }

    /// <summary>Open an image in the currently selected tab, regardless of the tab's previous kind.</summary>
    public bool ReplaceActiveWithImage(string path)
    {
        if (Active is null || ActiveIndex < 0) return false;
        var fullPath = Path.GetFullPath(path);
        _tabs[ActiveIndex] = new ImageTabState(Active.Id, fullPath);
        return true;
    }

    public bool ReplaceActiveBrowserFolder(string folder)
    {
        if (Active is not BrowserTabState browser) return false;
        _tabs[ActiveIndex] = browser with { Folder = Path.GetFullPath(folder), Title = BrowserTitle(folder) };
        return true;
    }

    /// <summary>Replace runtime state for a stable tab without changing its identity or position.</summary>
    public bool ReplaceTab(TabState tab)
    {
        var index = _tabs.FindIndex(t => t.Id == tab.Id);
        if (index < 0) return false;
        _tabs[index] = tab;
        return true;
    }

    public bool Select(Guid id)
    {
        var index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) return false;
        ActiveIndex = index;
        return true;
    }

    public bool SelectRelative(int delta)
    {
        if (_tabs.Count == 0 || delta == 0) return false;
        ActiveIndex = (ActiveIndex + delta) % _tabs.Count;
        if (ActiveIndex < 0) ActiveIndex += _tabs.Count;
        return true;
    }

    public bool CloseActive() => Active is not null && Close(Active.Id);

    public bool Close(Guid id, bool ensureHome = true)
    {
        var index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) return false;
        var closing = _tabs[index];
        _tabs.RemoveAt(index);
        _closed.Push(closing);
        TrimClosedHistory();

        if (_tabs.Count == 0)
        {
            ActiveIndex = -1;
            if (ensureHome) AddHome();
            return true;
        }

        if (index < ActiveIndex) ActiveIndex--;
        else if (index == ActiveIndex) ActiveIndex = Math.Min(index, _tabs.Count - 1);
        ActiveIndex = Math.Clamp(ActiveIndex, 0, _tabs.Count - 1);
        return true;
    }

    /// <summary>Remove without adding to closed history. Used only for a successful tab transfer.</summary>
    public TabState? RemoveForTransfer(Guid id)
    {
        var index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) return null;
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        if (_tabs.Count == 0) ActiveIndex = -1;
        else if (index < ActiveIndex) ActiveIndex--;
        else ActiveIndex = Math.Clamp(ActiveIndex, 0, _tabs.Count - 1);
        return tab;
    }

    /// <summary>Insert a transferred tab preserving its stable ID/state.</summary>
    public TabState InsertTransferred(TabState tab, int? index = null, bool activate = true)
    {
        var target = Math.Clamp(index ?? _tabs.Count, 0, _tabs.Count);
        _tabs.Insert(target, tab);
        if (activate) ActiveIndex = target;
        else if (ActiveIndex >= target) ActiveIndex++;
        return tab;
    }

    public void EnsureHomeIfEmpty()
    {
        if (_tabs.Count == 0) AddHome();
    }

    public void ResetForDetached(TabState tab, bool includeHome)
    {
        _tabs.Clear();
        _closed.Clear();
        ActiveIndex = -1;
        if (includeHome) AddHome();
        InsertTransferred(tab);
    }

    /// <summary>Replace the whole live workspace with a validated snapshot (used by startup/session restore).</summary>
    public void Reset(IEnumerable<TabState> tabs, int activeIndex = 0)
    {
        _tabs.Clear();
        _closed.Clear();
        ActiveIndex = -1;
        foreach (var tab in tabs) _tabs.Add(tab);
        if (_tabs.Count == 0)
        {
            AddHome();
            return;
        }
        ActiveIndex = Math.Clamp(activeIndex, 0, _tabs.Count - 1);
    }

    public TabState? RestoreClosed()
    {
        if (_closed.Count == 0) return null;
        var old = _closed.Pop();
        TabState? restored = old switch
        {
            HomeTabState => new HomeTabState(Guid.NewGuid()),
            ImageTabState image => new ImageTabState(Guid.NewGuid(), image.Path),
            BrowserTabState browser => new BrowserTabState(Guid.NewGuid(), browser.Folder),
            _ => null
        };
        return restored is null ? null : Add(restored);
    }

    /// <summary>
    /// Move a tab to an insertion slot in the range 0..Count. The slot is measured against the
    /// pre-removal strip (the same convention used by the UI midpoint hit-test), so moving a tab
    /// forward compensates for the removed element. The moved tab becomes active on completion.
    /// </summary>
    public bool Move(Guid id, int insertionIndex)
    {
        var oldIndex = _tabs.FindIndex(t => t.Id == id);
        if (oldIndex < 0 || _tabs.Count < 2) return false;
        insertionIndex = Math.Clamp(insertionIndex, 0, _tabs.Count);
        var adjustedIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
        adjustedIndex = Math.Clamp(adjustedIndex, 0, _tabs.Count - 1);
        if (oldIndex == adjustedIndex) return false;

        var tab = _tabs[oldIndex];
        _tabs.RemoveAt(oldIndex);
        adjustedIndex = Math.Clamp(adjustedIndex, 0, _tabs.Count);
        _tabs.Insert(adjustedIndex, tab);
        // A reordered tab owns the completed drag gesture and becomes active. This also
        // prevents the subsequent suppressed Click from leaving a different tab selected.
        ActiveIndex = adjustedIndex;
        return true;
    }

    private void TrimClosedHistory()
    {
        var limit = Math.Clamp(ClosedHistoryLimit, 1, 100);
        if (_closed.Count <= limit) return;
        var keepTopFirst = _closed.Take(limit).ToArray();
        _closed.Clear();
        for (var i = keepTopFirst.Length - 1; i >= 0; i--) _closed.Push(keepTopFirst[i]);
    }

    private TabState Add(TabState tab)
    {
        _tabs.Add(tab);
        ActiveIndex = _tabs.Count - 1;
        return tab;
    }

    private static string BrowserTitle(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return string.IsNullOrWhiteSpace(name) ? folder : name;
    }
}
