using Glide.Core.Workspace;

namespace Glide.Core.Navigation;

/// <summary>
/// Stage D step 6: the per-tab Back / Forward histories (one <see cref="NavigationHistory"/> per tab id), moved out of
/// <c>MainWindow.History.cs</c>. Creates a tab's history on first use from the copy that travelled with the tab, from a
/// restored Explorer tab's folder list, or empty; converts back to the session file's folder list; prunes closed tabs.
/// </summary>
public sealed class TabHistoryStore
{
    private readonly Dictionary<Guid, NavigationHistory> _histories = new();

    public int Count => _histories.Count;

    public bool TryGet(Guid tabId, out NavigationHistory history) => _histories.TryGetValue(tabId, out history!);

    public NavigationHistory? Find(Guid tabId) => _histories.TryGetValue(tabId, out var history) ? history : null;

    public bool Contains(Guid tabId) => _histories.ContainsKey(tabId);

    public void Clear() => _histories.Clear();

    /// <summary>The history of <paramref name="tab"/>, created on first use.</summary>
    public NavigationHistory For(TabState tab)
    {
        if (_histories.TryGetValue(tab.Id, out var history)) return history;
        history = tab.History is { } travelled ? NavigationHistory.FromState(travelled)
            : tab is BrowserTabState { Navigation: { } navigation } browser ? FromBrowserNavigation(browser, navigation)
            : new NavigationHistory();
        _histories[tab.Id] = history;
        return history;
    }

    /// <summary>Starts a tab's history over (empty).</summary>
    public void Reset(Guid tabId) => _histories[tabId] = new NavigationHistory();

    /// <summary>Removes a tab's history; returns its snapshot when it had entries (it travels with a transferred tab).</summary>
    public NavigationHistoryState? Take(Guid tabId) =>
        _histories.Remove(tabId, out var history) && history.Count > 0 ? history.Snapshot() : null;

    /// <summary>Drops histories of tabs that are no longer open.</summary>
    public void Prune(IReadOnlyCollection<TabState> liveTabs)
    {
        if (_histories.Count <= liveTabs.Count) return;
        var live = liveTabs.Select(t => t.Id).ToHashSet();
        foreach (var id in _histories.Keys.Where(id => !live.Contains(id)).ToArray()) _histories.Remove(id);
    }

    /// <summary>
    /// A restored Explorer tab (session file, older Glide) only knows its folders: they become Explorer entries
    /// (folders that are gone are dropped) with their remembered highlight as the selection. A restored view of the
    /// current folder is authoritative, so its older highlight is not used.
    /// </summary>
    public static NavigationHistory FromBrowserNavigation(BrowserTabState browser, BrowserNavigationState navigation)
    {
        var entries = new List<NavigationEntry>();
        var index = -1;
        for (var i = 0; i < navigation.History.Count; i++)
        {
            var folder = navigation.History[i];
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                var highlight = navigation.Highlights is { } highlights && i < highlights.Count && !string.IsNullOrWhiteSpace(highlights[i]) ? highlights[i] : null;
                if (i <= navigation.Index || index < 0) index = entries.Count;
                entries.Add(NavigationEntry.Explorer(Path.GetFullPath(folder), highlight));
            }
            catch { /* a damaged entry only loses that step */ }
        }
        if (entries.Count == 0) return new NavigationHistory();
        index = Math.Clamp(index, 0, entries.Count - 1);
        if (browser.View is not null) entries[index] = entries[index] with { SelectedPath = null };
        return NavigationHistory.FromState(new NavigationHistoryState(entries, index));
    }

    /// <summary>The Explorer folder history of a tab for the session file (its Explorer entries, newest last).</summary>
    public BrowserNavigationState? BrowserNavigationFor(Guid tabId)
    {
        if (!_histories.TryGetValue(tabId, out var history) || history.Count == 0) return null;
        var folders = new List<string>();
        var highlights = new List<string?>();
        var index = -1;
        for (var i = 0; i < history.Entries.Count; i++)
        {
            var entry = history.Entries[i];
            if (entry.Kind != NavigationEntryKind.Explorer || entry.Path is null) continue;
            if (i <= history.Index) index = folders.Count;
            folders.Add(entry.Path);
            highlights.Add(entry.SelectedPath);
        }
        return folders.Count == 0 ? null : new BrowserNavigationState(folders, Math.Max(0, index), highlights);
    }
}
